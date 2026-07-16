#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <climits>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <deque>
#include <fcntl.h>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>
#include <io.h>

#include "gpu.h"
#include "rife.h"

namespace
{
struct Options
{
    int width = 0;
    int height = 0;
    int multiplier = 0;
    int jobs = 2;
    std::wstring model;
};

bool ParsePositiveInt(const wchar_t* text, int& value)
{
    if (!text || !*text) return false;
    wchar_t* end = nullptr;
    errno = 0;
    const long parsed = std::wcstol(text, &end, 10);
    if (errno != 0 || !end || *end != L'\0' || parsed <= 0 || parsed > INT_MAX)
        return false;
    value = static_cast<int>(parsed);
    return true;
}

bool ParseOptions(int argc, wchar_t** argv, Options& options)
{
    for (int index = 1; index < argc; ++index)
    {
        const std::wstring argument = argv[index];
        if (index + 1 >= argc)
        {
            std::fwprintf(stderr, L"Missing value for %ls.\n", argument.c_str());
            return false;
        }

        const wchar_t* value = argv[++index];
        if (argument == L"--width")
        {
            if (!ParsePositiveInt(value, options.width)) return false;
        }
        else if (argument == L"--height")
        {
            if (!ParsePositiveInt(value, options.height)) return false;
        }
        else if (argument == L"--multiplier")
        {
            if (!ParsePositiveInt(value, options.multiplier)) return false;
        }
        else if (argument == L"--jobs")
        {
            if (!ParsePositiveInt(value, options.jobs)) return false;
        }
        else if (argument == L"--model")
        {
            options.model = value;
        }
        else
        {
            std::fwprintf(stderr, L"Unknown argument: %ls.\n", argument.c_str());
            return false;
        }
    }

    if (options.width <= 0 || options.height <= 0 ||
        (options.multiplier != 2 && options.multiplier != 4) ||
        options.jobs < 1 || options.jobs > 4 || options.model.empty())
    {
        std::fputs(
            "Usage: RifeProcessor --width W --height H --multiplier 2|4 "
            "--jobs 1..4 --model PATH\n",
            stderr);
        return false;
    }
    return true;
}

enum class ReadResult
{
    Frame,
    End,
    Error
};

ReadResult ReadFrame(std::vector<unsigned char>& frame)
{
    size_t offset = 0;
    while (offset < frame.size())
    {
        const size_t read = std::fread(frame.data() + offset, 1, frame.size() - offset, stdin);
        if (read == 0)
        {
            if (offset == 0 && std::feof(stdin)) return ReadResult::End;
            return ReadResult::Error;
        }
        offset += read;
    }
    return ReadResult::Frame;
}

bool IsSceneCut(
    const std::vector<unsigned char>& first,
    const std::vector<unsigned char>& second,
    int width,
    int height)
{
    const int stepX = std::max(1, width / 64);
    const int stepY = std::max(1, height / 36);
    std::array<long long, 48> firstHistogram{};
    std::array<long long, 48> secondHistogram{};
    long long absoluteDifference = 0;
    long long samplePixels = 0;

    for (int y = 0; y < height; y += stepY)
    {
        for (int x = 0; x < width; x += stepX)
        {
            const size_t offset = (static_cast<size_t>(y) * width + x) * 3;
            for (int channel = 0; channel < 3; ++channel)
            {
                const unsigned char a = first[offset + channel];
                const unsigned char b = second[offset + channel];
                absoluteDifference += std::abs(static_cast<int>(a) - static_cast<int>(b));
                // Convert BGR storage to the same three independent histogram banks.
                firstHistogram[channel * 16 + (a >> 4)]++;
                secondHistogram[channel * 16 + (b >> 4)]++;
            }
            ++samplePixels;
        }
    }

    if (samplePixels == 0) return false;
    long long histogramDifference = 0;
    for (size_t index = 0; index < firstHistogram.size(); ++index)
        histogramDifference += std::llabs(firstHistogram[index] - secondHistogram[index]);

    const double meanRgbDifference =
        absoluteDifference / static_cast<double>(samplePixels * 3);
    const double normalizedHistogramDifference =
        histogramDifference / static_cast<double>(samplePixels * 6);
    return meanRgbDifference >= 45.0 && normalizedHistogramDifference >= 0.25;
}

using Frame = std::shared_ptr<std::vector<unsigned char>>;

struct InferenceTask
{
    unsigned long long sequence = 0;
    Frame first;
    Frame second;
    float timestep = 0.5f;
};

class StreamingPipeline
{
public:
    StreamingPipeline(const RIFE& rife, int width, int height, int jobs)
        : rife_(rife), width_(width), height_(height),
          frameBytes_(static_cast<size_t>(width) * height * 3),
          maximumOutstanding_(static_cast<size_t>(std::max(8, jobs * 4)))
    {
        workers_.reserve(static_cast<size_t>(jobs));
        for (int index = 0; index < jobs; ++index)
            workers_.emplace_back([this] { WorkerLoop(); });
        writer_ = std::thread([this] { WriterLoop(); });
    }

    ~StreamingPipeline()
    {
        if (!joined_.load())
        {
            Abort("The interpolation pipeline ended unexpectedly.");
            Join();
        }
    }

    bool PublishFrame(unsigned long long sequence, Frame frame)
    {
        if (!ReserveSlot()) return false;
        {
            std::lock_guard lock(stateMutex_);
            results_.emplace(sequence, std::move(frame));
        }
        resultReady_.notify_all();
        return true;
    }

    bool QueueInference(
        unsigned long long sequence,
        Frame first,
        Frame second,
        float timestep)
    {
        if (!ReserveSlot()) return false;
        {
            std::lock_guard lock(taskMutex_);
            tasks_.push_back({sequence, std::move(first), std::move(second), timestep});
        }
        taskReady_.notify_one();
        return true;
    }

    bool Finish(unsigned long long totalFrames)
    {
        {
            std::lock_guard lock(stateMutex_);
            producerDone_ = true;
            totalFrames_ = totalFrames;
        }
        {
            std::lock_guard lock(taskMutex_);
            tasksDone_ = true;
        }
        taskReady_.notify_all();
        resultReady_.notify_all();
        Join();
        return !failed_.load();
    }

    void Abort(const char* message)
    {
        bool expected = false;
        if (failed_.compare_exchange_strong(expected, true))
        {
            std::lock_guard lock(errorMutex_);
            error_ = message;
        }
        taskReady_.notify_all();
        resultReady_.notify_all();
        slotAvailable_.notify_all();
    }

    std::string Error() const
    {
        std::lock_guard lock(errorMutex_);
        return error_;
    }

private:
    bool ReserveSlot()
    {
        std::unique_lock lock(stateMutex_);
        slotAvailable_.wait(lock, [this]
        {
            return failed_.load() || outstanding_ < maximumOutstanding_;
        });
        if (failed_.load()) return false;
        ++outstanding_;
        return true;
    }

    void WorkerLoop()
    {
        for (;;)
        {
            InferenceTask task;
            {
                std::unique_lock lock(taskMutex_);
                taskReady_.wait(lock, [this]
                {
                    return failed_.load() || !tasks_.empty() || tasksDone_;
                });
                if (failed_.load()) return;
                if (tasks_.empty())
                {
                    if (tasksDone_) return;
                    continue;
                }
                task = std::move(tasks_.front());
                tasks_.pop_front();
            }

            ncnn::Mat first(
                width_, height_, task.first->data(), static_cast<size_t>(3), 3);
            ncnn::Mat second(
                width_, height_, task.second->data(), static_cast<size_t>(3), 3);
            ncnn::Mat output(width_, height_, static_cast<size_t>(3), 3);
            if (rife_.process(first, second, task.timestep, output) != 0 ||
                !output.data || output.total() * output.elemsize < frameBytes_)
            {
                Abort("RIFE inference failed.");
                return;
            }

            auto pixels = std::make_shared<std::vector<unsigned char>>(frameBytes_);
            std::copy_n(
                static_cast<const unsigned char*>(output.data), frameBytes_, pixels->data());
            {
                std::lock_guard lock(stateMutex_);
                results_.emplace(task.sequence, std::move(pixels));
            }
            resultReady_.notify_all();
        }
    }

    void WriterLoop()
    {
        unsigned long long next = 0;
        for (;;)
        {
            Frame frame;
            {
                std::unique_lock lock(stateMutex_);
                resultReady_.wait(lock, [this, next]
                {
                    return failed_.load() || results_.contains(next) ||
                           (producerDone_ && next >= totalFrames_);
                });
                if (failed_.load()) return;
                if (producerDone_ && next >= totalFrames_) return;
                const auto found = results_.find(next);
                if (found == results_.end()) continue;
                frame = std::move(found->second);
                results_.erase(found);
            }

            size_t offset = 0;
            while (offset < frame->size())
            {
                const size_t written =
                    std::fwrite(frame->data() + offset, 1, frame->size() - offset, stdout);
                if (written == 0)
                {
                    Abort("The downstream video pipeline closed its input.");
                    return;
                }
                offset += written;
            }

            {
                std::lock_guard lock(stateMutex_);
                --outstanding_;
            }
            ++next;
            slotAvailable_.notify_all();
        }
    }

    void Join()
    {
        if (joined_.exchange(true)) return;
        {
            std::lock_guard lock(taskMutex_);
            tasksDone_ = true;
        }
        taskReady_.notify_all();
        for (auto& worker : workers_)
            if (worker.joinable()) worker.join();
        resultReady_.notify_all();
        if (writer_.joinable()) writer_.join();
    }

    const RIFE& rife_;
    const int width_;
    const int height_;
    const size_t frameBytes_;
    const size_t maximumOutstanding_;

    std::mutex taskMutex_;
    std::condition_variable taskReady_;
    std::deque<InferenceTask> tasks_;
    bool tasksDone_ = false;

    mutable std::mutex stateMutex_;
    std::condition_variable resultReady_;
    std::condition_variable slotAvailable_;
    std::map<unsigned long long, Frame> results_;
    size_t outstanding_ = 0;
    bool producerDone_ = false;
    unsigned long long totalFrames_ = 0;

    mutable std::mutex errorMutex_;
    std::string error_;
    std::atomic<bool> failed_ = false;
    std::atomic<bool> joined_ = false;
    std::vector<std::thread> workers_;
    std::thread writer_;
};
}

int wmain(int argc, wchar_t** argv)
{
    Options options;
    if (!ParseOptions(argc, argv, options)) return 2;
    if (_setmode(_fileno(stdin), _O_BINARY) == -1 ||
        _setmode(_fileno(stdout), _O_BINARY) == -1)
    {
        std::fputs("Could not switch the frame pipes to binary mode.\n", stderr);
        return 3;
    }
    std::setvbuf(stdin, nullptr, _IOFBF, 1024 * 1024);
    std::setvbuf(stdout, nullptr, _IOFBF, 1024 * 1024);

    const size_t frameBytes =
        static_cast<size_t>(options.width) * options.height * 3;
    if (frameBytes == 0 || frameBytes > static_cast<size_t>(INT_MAX) * 8)
    {
        std::fputs("The requested frame size is not supported.\n", stderr);
        return 4;
    }

    ncnn::create_gpu_instance();
    int exitCode = 0;
    {
        const int gpu = ncnn::get_default_gpu_index();
        if (gpu < 0)
        {
            std::fputs("No Vulkan GPU was available for RIFE interpolation.\n", stderr);
            ncnn::destroy_gpu_instance();
            return 5;
        }

        const bool uhd = options.width > 1920 || options.height > 1080;
        RIFE rife(gpu, false, false, uhd, 1, false, true);
        if (rife.load(options.model) != 0)
        {
            std::fputs("The RIFE v4 model could not be loaded.\n", stderr);
            exitCode = 6;
        }
        else
        {
            StreamingPipeline pipeline(
                rife, options.width, options.height, options.jobs);
            auto previous = std::make_shared<std::vector<unsigned char>>(frameBytes);
            const ReadResult firstRead = ReadFrame(*previous);
            if (firstRead == ReadResult::Error)
            {
                pipeline.Abort("The decoder ended in the middle of a source frame.");
                exitCode = 7;
            }
            else if (firstRead == ReadResult::End)
            {
                if (!pipeline.Finish(0)) exitCode = 8;
            }
            else
            {
                unsigned long long sequence = 0;
                for (;;)
                {
                    auto next = std::make_shared<std::vector<unsigned char>>(frameBytes);
                    const ReadResult read = ReadFrame(*next);
                    if (read == ReadResult::Error)
                    {
                        pipeline.Abort("The decoder ended in the middle of a source frame.");
                        exitCode = 7;
                        break;
                    }
                    if (read == ReadResult::End)
                    {
                        for (int duplicate = 0; duplicate < options.multiplier; ++duplicate)
                        {
                            if (!pipeline.PublishFrame(sequence++, previous)) break;
                        }
                        if (!pipeline.Finish(sequence)) exitCode = 8;
                        break;
                    }

                    const bool sceneCut =
                        IsSceneCut(*previous, *next, options.width, options.height);
                    if (!pipeline.PublishFrame(sequence++, previous))
                    {
                        exitCode = 8;
                        break;
                    }
                    for (int step = 1; step < options.multiplier; ++step)
                    {
                        const bool queued = sceneCut
                            ? pipeline.PublishFrame(sequence++, previous)
                            : pipeline.QueueInference(
                                sequence++, previous, next,
                                step / static_cast<float>(options.multiplier));
                        if (!queued)
                        {
                            exitCode = 8;
                            break;
                        }
                    }
                    if (exitCode != 0) break;
                    previous = std::move(next);
                }
            }

            if (exitCode != 0)
            {
                const std::string error = pipeline.Error();
                if (!error.empty()) std::fprintf(stderr, "%s\n", error.c_str());
            }
        }
    }
    ncnn::destroy_gpu_instance();
    return exitCode;
}
