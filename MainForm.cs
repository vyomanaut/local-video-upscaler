using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace RtxLocalVideo;

internal sealed class MainForm : Form
{
    private static readonly Color PageColor = Color.FromArgb(13, 17, 23);
    private static readonly Color PanelColor = Color.FromArgb(22, 27, 34);
    private static readonly Color BorderColor = Color.FromArgb(48, 54, 61);
    private static readonly Color MutedColor = Color.FromArgb(139, 148, 158);
    private static readonly Color AccentColor = Color.FromArgb(56, 139, 253);

    private readonly DropPanel dropPanel = new();
    private readonly Label fileLabel = new();
    private readonly Label fileHint = new();
    private readonly Label gpuValue = new();
    private readonly Label driverValue = new();
    private readonly Label inputValue = new();
    private readonly ComboBox scaleCombo = new();
    private readonly TrackBar vsrQualitySlider = new();
    private readonly Label vsrQualityValue = new();
    private readonly Button vsrQualityInfoButton = new();
    private readonly ComboBox encodeQualityCombo = new();
    private readonly CheckBox frameInterpolationCheck = new();
    private readonly ComboBox frameMultiplierCombo = new();
    private readonly Label effectiveFpsValue = new();
    private readonly TextBox rangeStartText = new();
    private readonly TextBox rangeEndText = new();
    private readonly Label rangeSummary = new();
    private readonly Button resetRangeButton = new();
    private readonly Label outputValue = new();
    private readonly Label statusLabel = new();
    private readonly ProgressBar progressBar = new();
    private readonly Button exportButton = new();
    private readonly Button cancelButton = new();
    private readonly Button outputButton = new();
    private readonly Button settingsButton = new();
    private readonly ToolTip toolTip = new();

    private string? selectedVideo;
    private string? outputPath;
    private VideoInfo? videoInfo;
    private SystemStatus? systemStatus;
    private CancellationTokenSource? exportCancellation;
    private bool vsrQualityControlAvailable;
    private readonly string? initialMediaPath;

    public MainForm(string? initialMediaPath = null)
    {
        this.initialMediaPath = initialMediaPath;
        Text = "LocalVSR";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(840, 580);
        ClientSize = new Size(960, 580);
        WindowState = FormWindowState.Normal;
        MaximizeBox = false;
        BackColor = PageColor;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F);
        AllowDrop = true;
        toolTip.InitialDelay = 250;
        toolTip.ReshowDelay = 100;
        toolTip.AutoPopDelay = 8000;
        toolTip.ShowAlways = true;

        BuildUi();
        WireEvents();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) toolTip.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Half the working width by half the working height = one-quarter area.
        var workingArea = Screen.FromControl(this).WorkingArea;
        var maximumWidth = Math.Max(320, workingArea.Width - 40);
        var maximumHeight = Math.Max(320, workingArea.Height - 40);
        var width = Math.Clamp(workingArea.Width / 2, Math.Min(MinimumSize.Width, maximumWidth), maximumWidth);
        var height = Math.Clamp(workingArea.Height / 2, Math.Min(MinimumSize.Height, maximumHeight), maximumHeight);
        Bounds = new Rectangle(
            workingArea.Left + (workingArea.Width - width) / 2,
            workingArea.Top + (workingArea.Height - height) / 2,
            width,
            height);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        systemStatus = await SystemProbe.ReadAsync();
        gpuValue.Text = systemStatus.GpuName ?? "Not detected";
        gpuValue.ForeColor = systemStatus.HasRtxGpu ? AccentColor : Color.Orange;
        driverValue.Text = systemStatus.DriverVersion ?? "Not detected";
        settingsButton.Enabled = systemStatus.NvidiaAppPath is not null;
        exportButton.Enabled = videoInfo is not null && systemStatus.HasRtxGpu && AppPaths.AllDependenciesPresent;

        var vsrStatus = await Task.Run(NvidiaVsrSettings.Probe);
        vsrQualityControlAvailable = vsrStatus.IsAvailable;
        vsrQualitySlider.Enabled = vsrQualityControlAvailable;
        if (vsrQualityControlAvailable && vsrStatus.MaximumLevel < 5)
        {
            // Older drivers expose fixed levels 1–4 but not Auto (value 5).
            vsrQualitySlider.Minimum = 1;
        }
        else if (!vsrQualityControlAvailable)
        {
            vsrQualityValue.Text = "NVIDIA";
            vsrQualitySlider.AccessibleDescription =
                "Quality override unavailable on this driver; the NVIDIA system setting will be used.";
        }
        toolTip.SetToolTip(vsrQualitySlider, vsrQualityControlAvailable
            ? $"Current NVIDIA setting: {NvidiaVsrSettings.FormatLevel(vsrStatus.CurrentLevel)}. A selected level is applied only during export, then restored."
            : $"Quality override unavailable; the NVIDIA system setting will be used. {vsrStatus.Error}");

        if (!AppPaths.AllDependenciesPresent)
        {
            statusLabel.Text = "Required FFmpeg or VSR worker files are missing.";
            statusLabel.ForeColor = Color.Orange;
        }

        if (initialMediaPath is not null)
            await SelectVideoAsync(initialMediaPath);
    }

    private void BuildUi()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 12, 20, 12),
            RowCount = 12,
            ColumnCount = 1,
            BackColor = PageColor,
            AutoScroll = true
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        Controls.Add(page);

        var heading = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0)
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var eyebrow = new Label
        {
            Text = "LOCALVSR",
            AutoSize = true,
            ForeColor = AccentColor,
            Font = new Font("Segoe UI Semibold", 9F),
            Margin = new Padding(0, 0, 0, 3)
        };
        heading.Controls.Add(eyebrow, 0, 0);
        heading.SetColumnSpan(eyebrow, 2);
        var headingTitle = new Label
        {
            Text = "Upscale a video using your local NVIDIA GPU",
            AutoSize = false,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 18F),
            Margin = new Padding(0)
        };
        heading.Controls.Add(headingTitle, 0, 1);
        StyleSecondaryButton(settingsButton, "NVIDIA settings");
        settingsButton.Margin = new Padding(8, 0, 0, 0);
        settingsButton.Enabled = false;
        heading.Controls.Add(settingsButton, 1, 1);
        page.Controls.Add(heading, 0, 0);

        dropPanel.Dock = DockStyle.Fill;
        dropPanel.BackColor = PanelColor;
        dropPanel.BorderColor = Color.White;
        dropPanel.AllowDrop = true;
        dropPanel.Cursor = Cursors.Hand;
        dropPanel.Padding = new Padding(18);
        var dropContents = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Color.Transparent
        };
        dropContents.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        dropContents.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        dropContents.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dropContents.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        dropContents.Controls.Add(new Label
        {
            Text = "⇧",
            AutoSize = true,
            Anchor = AnchorStyles.Bottom,
            ForeColor = AccentColor,
            Font = new Font("Segoe UI Symbol", 22F)
        }, 0, 0);
        fileLabel.Text = "Drop a video or image to upscale";
        fileLabel.AutoSize = false;
        fileLabel.Dock = DockStyle.Fill;
        fileLabel.AutoEllipsis = true;
        fileLabel.TextAlign = ContentAlignment.MiddleCenter;
        fileLabel.Font = new Font("Segoe UI Semibold", 13F);
        fileLabel.Margin = new Padding(0, 2, 0, 2);
        fileHint.Text = "or click to choose a local file";
        fileHint.AutoSize = true;
        fileHint.Anchor = AnchorStyles.None;
        fileHint.ForeColor = MutedColor;
        dropContents.Controls.Add(fileLabel, 0, 1);
        dropContents.Controls.Add(fileHint, 0, 2);
        dropPanel.Controls.Add(dropContents);
        page.Controls.Add(dropPanel, 0, 2);

        var optionsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 5,
            RowCount = 2,
            BackColor = PanelColor,
            Padding = new Padding(12, 6, 12, 6)
        };
        optionsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        optionsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        optionsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        optionsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        optionsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 19));
        optionsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 21));
        optionsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        AddOptionLabel(optionsPanel, 0, "INPUT");
        AddOptionLabel(optionsPanel, 1, "UPSCALE");
        AddVsrQualityHeader(optionsPanel, 2);
        AddOptionLabel(optionsPanel, 3, "ENCODE QUALITY");
        AddOptionLabel(optionsPanel, 4, "OUTPUT");

        inputValue.Text = "Choose media";
        StyleOptionValue(inputValue);
        optionsPanel.Controls.Add(inputValue, 0, 1);

        StyleComboBox(scaleCombo);
        scaleCombo.Enabled = false;
        optionsPanel.Controls.Add(scaleCombo, 1, 1);

        var vsrQualityCell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(4, 0, 5, 0)
        };
        vsrQualityCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        vsrQualityCell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        vsrQualitySlider.Minimum = 0;
        vsrQualitySlider.Maximum = 4;
        vsrQualitySlider.Value = 4;
        vsrQualitySlider.TickFrequency = 1;
        vsrQualitySlider.SmallChange = 1;
        vsrQualitySlider.LargeChange = 1;
        vsrQualitySlider.TickStyle = TickStyle.BottomRight;
        vsrQualitySlider.AutoSize = false;
        vsrQualitySlider.Height = 40;
        vsrQualitySlider.Dock = DockStyle.Fill;
        vsrQualitySlider.BackColor = PanelColor;
        vsrQualitySlider.ForeColor = Color.White;
        vsrQualitySlider.Margin = new Padding(0);
        vsrQualitySlider.Enabled = false;
        vsrQualitySlider.AccessibleName = "VSR quality";
        vsrQualitySlider.AccessibleDescription = "Auto, followed by fixed quality levels 1 through 4.";
        vsrQualityValue.Text = "4";
        vsrQualityValue.Dock = DockStyle.Fill;
        vsrQualityValue.TextAlign = ContentAlignment.MiddleCenter;
        vsrQualityValue.ForeColor = Color.White;
        vsrQualityValue.Font = new Font("Segoe UI Semibold", 9F);
        vsrQualityValue.Margin = new Padding(0);
        toolTip.SetToolTip(vsrQualitySlider,
            "Level 4 is NVIDIA's highest fixed VSR quality. The previous NVIDIA setting is restored after export.");
        vsrQualityCell.Controls.Add(vsrQualitySlider, 0, 0);
        vsrQualityCell.Controls.Add(vsrQualityValue, 1, 0);
        optionsPanel.Controls.Add(vsrQualityCell, 2, 1);

        StyleComboBox(encodeQualityCombo);
        encodeQualityCombo.Items.AddRange(["Highest  (CQ 16)", "Balanced  (CQ 21)", "Smaller  (CQ 24)"]);
        encodeQualityCombo.SelectedIndex = 1;
        optionsPanel.Controls.Add(encodeQualityCombo, 3, 1);

        var outputCell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(7, 3, 0, 0) };
        outputCell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputCell.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputValue.Text = "Automatic";
        outputValue.AutoEllipsis = true;
        outputValue.AutoSize = false;
        outputValue.Dock = DockStyle.Fill;
        outputValue.TextAlign = ContentAlignment.MiddleLeft;
        outputValue.ForeColor = Color.White;
        StyleSecondaryButton(outputButton, "…");
        outputButton.Padding = new Padding(7, 3, 7, 3);
        outputButton.Enabled = false;
        outputCell.Controls.Add(outputValue, 0, 0);
        outputCell.Controls.Add(outputButton, 1, 0);
        optionsPanel.Controls.Add(outputCell, 4, 1);
        page.Controls.Add(optionsPanel, 0, 4);

        var frameRatePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = PanelColor,
            Padding = new Padding(12, 5, 12, 5)
        };
        frameRatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        frameRatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        frameRatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        frameRatePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frameInterpolationCheck.Text = string.Empty;
        frameInterpolationCheck.AutoSize = true;
        frameInterpolationCheck.Anchor = AnchorStyles.Left;
        frameInterpolationCheck.Enabled = false;
        frameInterpolationCheck.Margin = new Padding(0, 3, 4, 0);
        frameInterpolationCheck.AccessibleName = "Frame multiplication";
        frameInterpolationCheck.AccessibleDescription =
            "Generate motion-interpolated frames and increase the output frame rate.";
        var frameInterpolationLabel = new Label
        {
            Text = "Frame multiplication",
            AutoSize = true,
            ForeColor = Color.White,
            Margin = new Padding(0, 2, 14, 0),
            Cursor = Cursors.Hand
        };
        frameInterpolationLabel.Click += (_, _) =>
        {
            if (frameInterpolationCheck.Enabled)
                frameInterpolationCheck.Checked = !frameInterpolationCheck.Checked;
        };
        toolTip.SetToolTip(frameInterpolationCheck,
            "Uses a local Vulkan AI model to create frames between originals. GPU usage and export time will increase.");
        toolTip.SetToolTip(frameInterpolationLabel,
            "Uses a local Vulkan AI model to create frames between originals. GPU usage and export time will increase.");
        var frameInterpolationToggle = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        frameInterpolationToggle.Controls.Add(frameInterpolationCheck);
        frameInterpolationToggle.Controls.Add(frameInterpolationLabel);
        frameRatePanel.Controls.Add(frameInterpolationToggle, 0, 0);

        StyleComboBox(frameMultiplierCombo);
        frameMultiplierCombo.Margin = new Padding(0, 0, 8, 0);
        frameMultiplierCombo.Visible = false;
        frameMultiplierCombo.Enabled = false;
        frameRatePanel.Controls.Add(frameMultiplierCombo, 1, 0);

        effectiveFpsValue.AutoSize = true;
        effectiveFpsValue.Anchor = AnchorStyles.Left;
        effectiveFpsValue.ForeColor = MutedColor;
        effectiveFpsValue.Margin = new Padding(0, 2, 0, 0);
        effectiveFpsValue.Visible = false;
        frameRatePanel.Controls.Add(effectiveFpsValue, 2, 0);
        page.Controls.Add(frameRatePanel, 0, 6);

        var rangePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 7,
            RowCount = 1,
            BackColor = PanelColor,
            Padding = new Padding(12, 5, 12, 5)
        };
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rangePanel.Controls.Add(new Label
        {
            Text = "Range",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.White,
            Margin = new Padding(0, 2, 10, 0)
        }, 0, 0);
        AddRangeLabel(rangePanel, 1, "Start");
        StyleRangeTextBox(rangeStartText, "Start time");
        rangePanel.Controls.Add(rangeStartText, 2, 0);
        AddRangeLabel(rangePanel, 3, "End");
        StyleRangeTextBox(rangeEndText, "End time");
        rangePanel.Controls.Add(rangeEndText, 4, 0);
        rangeSummary.AutoSize = false;
        rangeSummary.Dock = DockStyle.Fill;
        rangeSummary.TextAlign = ContentAlignment.MiddleLeft;
        rangeSummary.ForeColor = MutedColor;
        rangeSummary.Margin = new Padding(8, 2, 6, 0);
        rangeSummary.Text = "Choose a video";
        rangePanel.Controls.Add(rangeSummary, 5, 0);
        StyleSecondaryButton(resetRangeButton, "Full video");
        resetRangeButton.Padding = new Padding(7, 2, 7, 2);
        resetRangeButton.Margin = new Padding(4, 0, 0, 0);
        resetRangeButton.Enabled = false;
        toolTip.SetToolTip(rangePanel,
            "Use seconds, MM:SS, or HH:MM:SS. Decimals are supported.");
        toolTip.SetToolTip(rangeStartText,
            "Start timestamp: seconds, MM:SS, or HH:MM:SS.");
        toolTip.SetToolTip(rangeEndText,
            "End timestamp: seconds, MM:SS, or HH:MM:SS.");
        rangePanel.Controls.Add(resetRangeButton, 6, 0);
        page.Controls.Add(rangePanel, 0, 8);

        var diagnostics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 4,
            BackColor = PanelColor,
            Padding = new Padding(12, 6, 12, 6)
        };
        diagnostics.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        diagnostics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        diagnostics.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        diagnostics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        AddDiagnostic(diagnostics, 0, "GPU", gpuValue);
        AddDiagnostic(diagnostics, 2, "DRIVER", driverValue);
        page.Controls.Add(diagnostics, 0, 10);

        var actionArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 3,
            RowCount = 2,
            Padding = new Padding(0, 8, 0, 0)
        };
        actionArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        actionArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actionArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressBar.Dock = DockStyle.Fill;
        progressBar.Height = 6;
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Margin = new Padding(0, 5, 16, 0);
        actionArea.Controls.Add(progressBar, 0, 0);
        StyleSecondaryButton(cancelButton, "Cancel");
        cancelButton.Enabled = false;
        actionArea.Controls.Add(cancelButton, 1, 0);
        exportButton.Text = "Export upscaled copy";
        exportButton.AutoSize = true;
        exportButton.Padding = new Padding(12, 6, 12, 6);
        exportButton.FlatStyle = FlatStyle.Flat;
        exportButton.FlatAppearance.BorderSize = 0;
        exportButton.BackColor = AccentColor;
        exportButton.ForeColor = Color.White;
        exportButton.Font = new Font("Segoe UI Semibold", 10F);
        exportButton.Enabled = false;
        exportButton.Cursor = Cursors.Hand;
        exportButton.Margin = new Padding(8, 0, 0, 0);
        actionArea.Controls.Add(exportButton, 2, 0);
        statusLabel.Text = "Choose a video or image to begin";
        statusLabel.AutoSize = false;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.TopLeft;
        statusLabel.ForeColor = MutedColor;
        statusLabel.Margin = new Padding(0);
        actionArea.Controls.Add(statusLabel, 0, 1);
        actionArea.SetColumnSpan(statusLabel, 3);
        page.Controls.Add(actionArea, 0, 11);
    }

    private void WireEvents()
    {
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        dropPanel.DragEnter += OnDragEnter;
        dropPanel.DragDrop += OnDragDrop;
        dropPanel.DragLeave += (_, _) => ResetDropBorder();
        dropPanel.Click += (_, _) => ChooseFile();
        foreach (Control child in dropPanel.Controls) WireDropThrough(child);

        scaleCombo.SelectedIndexChanged += (_, _) => UpdateOutputPath();
        vsrQualitySlider.ValueChanged += (_, _) => UpdateVsrQualityDisplay();
        frameInterpolationCheck.CheckedChanged += (_, _) =>
        {
            UpdateFrameRateControls();
            UpdateOutputPath();
        };
        frameMultiplierCombo.SelectedIndexChanged += (_, _) =>
        {
            UpdateEffectiveFps();
            UpdateOutputPath();
        };
        rangeStartText.TextChanged += (_, _) =>
        {
            rangeStartText.ForeColor = Color.White;
            UpdateOutputPath();
        };
        rangeEndText.TextChanged += (_, _) =>
        {
            rangeEndText.ForeColor = Color.White;
            UpdateOutputPath();
        };
        resetRangeButton.Click += (_, _) => ResetMediaRange();
        outputButton.Click += (_, _) => ChooseOutput();
        exportButton.Click += async (_, _) => await ExportAsync();
        cancelButton.Click += (_, _) => exportCancellation?.Cancel();
        settingsButton.Click += (_, _) => OpenNvidiaSettings();
        FormClosing += (_, e) =>
        {
            if (exportCancellation is null) return;
            var answer = MessageBox.Show(this, "Cancel the current export and close?", "Export running",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.No) e.Cancel = true;
            else exportCancellation.Cancel();
        };
    }

    private void WireDropThrough(Control control)
    {
        control.AllowDrop = true;
        control.Click += (_, _) => ChooseFile();
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        control.DragLeave += (_, _) => ResetDropBorder();
        foreach (Control child in control.Controls) WireDropThrough(child);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = exportCancellation is null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        dropPanel.BorderColor = Color.White;
        dropPanel.Invalidate();
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        ResetDropBorder();
        if (exportCancellation is null && e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            _ = SelectVideoAsync(files[0]);
    }

    private void ChooseFile()
    {
        if (exportCancellation is not null) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a video or image to upscale",
            Filter = "Videos and images|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.m4v;*.wmv;*.ts;*.m2ts;*.ogv;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Video files|*.mp4;*.mkv;*.webm;*.avi;*.mov;*.m4v;*.wmv;*.ts;*.m2ts;*.ogv|Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = SelectVideoAsync(dialog.FileName);
    }

    private async Task SelectVideoAsync(string path)
    {
        if (!File.Exists(path)) return;
        selectedVideo = Path.GetFullPath(path);
        videoInfo = null;
        scaleCombo.Items.Clear();
        scaleCombo.Enabled = false;
        frameInterpolationCheck.Checked = false;
        frameInterpolationCheck.Enabled = false;
        frameMultiplierCombo.Items.Clear();
        rangeStartText.Enabled = false;
        rangeEndText.Enabled = false;
        resetRangeButton.Enabled = false;
        rangeSummary.Text = "Inspecting…";
        UpdateFrameRateControls();
        exportButton.Enabled = false;
        fileLabel.Text = Path.GetFileName(path);
        fileHint.Text = "Inspecting media…";
        statusLabel.Text = "Reading media information…";
        statusLabel.ForeColor = MutedColor;
        dropPanel.BorderColor = Color.White;
        dropPanel.Invalidate();

        try
        {
            videoInfo = await VideoProbe.ReadAsync(selectedVideo);
            var scales = VideoProbe.GetScaleChoices(videoInfo);
            foreach (var scale in scales) scaleCombo.Items.Add(scale);
            if (scaleCombo.Items.Count == 0)
                throw new InvalidOperationException("This file is already too large for a higher VSR export within the 4K limit.");

            scaleCombo.SelectedIndex = scales
                .Select((scale, index) => (scale, index))
                .FirstOrDefault(item => item.scale.Factor == 2).index;
            scaleCombo.Enabled = true;
            outputButton.Enabled = true;
            inputValue.Text = $"{videoInfo.Width} × {videoInfo.Height}";
            fileHint.Text = videoInfo.IsImage
                ? $"Still image  •  {FormatBytes(new FileInfo(path).Length)}"
                : $"{FormatDuration(videoInfo.Duration)}  •  {videoInfo.FramesPerSecond:0.##} fps  •  {FormatBytes(new FileInfo(path).Length)}";
            statusLabel.Text = "Ready. The original file will not be modified.";
            exportButton.Enabled = systemStatus?.HasRtxGpu == true && AppPaths.AllDependenciesPresent;
            encodeQualityCombo.Enabled = !videoInfo.IsImage;
            ConfigureMediaRange();
            PopulateFrameMultiplierChoices();
            UpdateOutputPath();
        }
        catch (Exception ex)
        {
            statusLabel.Text = ex.Message;
            statusLabel.ForeColor = Color.Orange;
            inputValue.Text = "Unsupported";
            rangeSummary.Text = "Unavailable";
        }
    }

    private void UpdateOutputPath()
    {
        if (selectedVideo is null || scaleCombo.SelectedItem is not ScaleChoice scale) return;
        var directory = Path.GetDirectoryName(selectedVideo)!;
        var stem = Path.GetFileNameWithoutExtension(selectedVideo);
        var extension = videoInfo?.IsImage == true ? ".png" : ".mkv";
        var frameRateSuffix = GetFrameMultiplier() > 1 ? $"-FPS-{GetFrameMultiplier()}x" : string.Empty;
        var rangeSuffix = TryGetMediaRange(out var range, out _) && IsCustomRange(range) ? "-clip" : string.Empty;
        outputPath = Path.Combine(directory, $"{stem}.VSR-{scale.Factor:0.#}x{frameRateSuffix}{rangeSuffix}{extension}");
        outputValue.Text = Path.GetFileName(outputPath);
        outputValue.Tag = outputPath;
    }

    private void ChooseOutput()
    {
        if (selectedVideo is null || outputPath is null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Save upscaled copy",
            Filter = videoInfo?.IsImage == true ? "PNG image|*.png" : "Matroska video|*.mkv",
            FileName = Path.GetFileName(outputPath),
            InitialDirectory = Path.GetDirectoryName(outputPath),
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        outputPath = dialog.FileName;
        outputValue.Text = Path.GetFileName(outputPath);
        outputValue.Tag = outputPath;
    }

    private async Task ExportAsync()
    {
        if (selectedVideo is null || outputPath is null || videoInfo is null ||
            scaleCombo.SelectedItem is not ScaleChoice scale) return;

        if (!TryGetMediaRange(out var range, out var rangeError))
        {
            rangeStartText.ForeColor = Color.Orange;
            rangeEndText.ForeColor = Color.Orange;
            MessageBox.Show(this, rangeError, "Invalid process range",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (File.Exists(outputPath))
        {
            var overwrite = MessageBox.Show(this, "The output file already exists. Replace it?",
                "Replace output", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (overwrite != DialogResult.Yes) return;
        }

        var quality = encodeQualityCombo.SelectedIndex switch { 0 => 16, 2 => 24, _ => 21 };
        var frameMultiplier = GetFrameMultiplier();
        int? vsrLevel = vsrQualityControlAvailable
            ? vsrQualitySlider.Value == 0 ? 5 : vsrQualitySlider.Value
            : null;
        exportCancellation = new CancellationTokenSource();
        SetExportingState(true);
        var progress = new Progress<ExportProgress>(update =>
        {
            progressBar.Value = (int)Math.Clamp(Math.Round(update.Percent), 0, 100);
            statusLabel.Text = update.Message;
            statusLabel.ForeColor = MutedColor;
        });

        string? restoreWarning = null;
        try
        {
            TemporaryNvidiaVsrLevel? levelOverride = null;
            try
            {
                if (vsrLevel.HasValue)
                {
                    statusLabel.Text = $"Applying NVIDIA VSR {NvidiaVsrSettings.FormatLevel(vsrLevel.Value)}…";
                    levelOverride = NvidiaVsrSettings.ApplyTemporary(vsrLevel.Value);
                }

                await ExportService.RunAsync(
                    selectedVideo, outputPath, videoInfo, scale, quality, frameMultiplier,
                    range, progress, exportCancellation.Token);
            }
            finally
            {
                levelOverride?.Dispose();
                restoreWarning = levelOverride?.RestoreWarning;
            }

            progressBar.Value = 100;
            statusLabel.Text = $"Saved {Path.GetFileName(outputPath)}";
            statusLabel.ForeColor = AccentColor;
            var completionMessage = $"Upscaled copy saved to:\n{outputPath}";
            if (restoreWarning is not null) completionMessage += $"\n\n{restoreWarning}";
            MessageBox.Show(this, completionMessage,
                "Export complete", MessageBoxButtons.OK,
                restoreWarning is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException)
        {
            progressBar.Value = 0;
            statusLabel.Text = "Export cancelled; the original is unchanged.";
            statusLabel.ForeColor = MutedColor;
        }
        catch (Exception ex)
        {
            progressBar.Value = 0;
            statusLabel.Text = "Export failed. See the error dialog.";
            statusLabel.ForeColor = Color.Orange;
            var errorMessage = restoreWarning is null ? ex.Message : $"{ex.Message}\n\n{restoreWarning}";
            MessageBox.Show(this, errorMessage, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            exportCancellation.Dispose();
            exportCancellation = null;
            SetExportingState(false);
        }
    }

    private void SetExportingState(bool exporting)
    {
        exportButton.Enabled = !exporting && videoInfo is not null && systemStatus?.HasRtxGpu == true;
        cancelButton.Enabled = exporting;
        scaleCombo.Enabled = !exporting && videoInfo is not null;
        vsrQualitySlider.Enabled = !exporting && vsrQualityControlAvailable;
        encodeQualityCombo.Enabled = !exporting && videoInfo?.IsImage != true;
        frameInterpolationCheck.Enabled = !exporting && videoInfo?.IsImage == false && frameMultiplierCombo.Items.Count > 0;
        frameMultiplierCombo.Enabled = !exporting && frameInterpolationCheck.Checked && frameInterpolationCheck.Enabled;
        rangeStartText.Enabled = !exporting && videoInfo?.IsImage == false;
        rangeEndText.Enabled = !exporting && videoInfo?.IsImage == false;
        resetRangeButton.Enabled = !exporting && videoInfo?.IsImage == false;
        outputButton.Enabled = !exporting && videoInfo is not null;
        dropPanel.Cursor = exporting ? Cursors.WaitCursor : Cursors.Hand;
    }

    private void OpenNvidiaSettings()
    {
        if (systemStatus?.NvidiaAppPath is null) return;
        Process.Start(new ProcessStartInfo(systemStatus.NvidiaAppPath) { UseShellExecute = true });
        MessageBox.Show(this,
            "Open System → Video → RTX video enhancement and turn Super Resolution on.",
            "NVIDIA settings", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ResetDropBorder()
    {
        dropPanel.BorderColor = Color.White;
        dropPanel.Invalidate();
    }

    private static void AddOptionLabel(TableLayoutPanel panel, int column, string text)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8F),
            Margin = new Padding(column == 0 ? 0 : 7, 0, 0, 3)
        }, column, 0);
    }

    private void AddVsrQualityHeader(TableLayoutPanel panel, int column)
    {
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(7, 0, 0, 0),
            Padding = new Padding(0)
        };
        header.Controls.Add(new Label
        {
            Text = "VSR QUALITY",
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8F),
            Margin = new Padding(0, 1, 4, 0)
        });
        vsrQualityInfoButton.Text = "i";
        vsrQualityInfoButton.AutoSize = false;
        vsrQualityInfoButton.Size = new Size(18, 18);
        vsrQualityInfoButton.Margin = new Padding(0);
        vsrQualityInfoButton.Padding = new Padding(0);
        vsrQualityInfoButton.FlatStyle = FlatStyle.Flat;
        vsrQualityInfoButton.FlatAppearance.BorderColor = MutedColor;
        vsrQualityInfoButton.FlatAppearance.BorderSize = 1;
        vsrQualityInfoButton.BackColor = PanelColor;
        vsrQualityInfoButton.ForeColor = MutedColor;
        vsrQualityInfoButton.Font = new Font("Segoe UI Semibold", 7.5F);
        vsrQualityInfoButton.Cursor = Cursors.Help;
        vsrQualityInfoButton.TabStop = false;
        vsrQualityInfoButton.AccessibleName = "VSR quality information";
        toolTip.SetToolTip(vsrQualityInfoButton,
            "GPU utilization will increase for higher quality.");
        header.Controls.Add(vsrQualityInfoButton);
        panel.Controls.Add(header, column, 0);
    }

    private void UpdateVsrQualityDisplay()
    {
        vsrQualityValue.Text = vsrQualitySlider.Value == 0 ? "Auto" : vsrQualitySlider.Value.ToString();
    }

    private void PopulateFrameMultiplierChoices()
    {
        frameMultiplierCombo.Items.Clear();
        if (videoInfo?.IsImage != false || !AppPaths.FrameInterpolationDependenciesPresent)
        {
            frameInterpolationCheck.Enabled = false;
            if (!AppPaths.FrameInterpolationDependenciesPresent)
            {
                toolTip.SetToolTip(frameInterpolationCheck,
                    "Frame multiplication is unavailable because its local AI runtime or model is missing.");
            }
            UpdateFrameRateControls();
            return;
        }

        foreach (var multiplier in new[] { 2, 4 })
        {
            if (videoInfo.FramesPerSecond * multiplier <= 240.01)
                frameMultiplierCombo.Items.Add(new FrameMultiplierChoice(multiplier));
        }

        frameMultiplierCombo.SelectedIndex = frameMultiplierCombo.Items.Count > 0 ? 0 : -1;
        frameInterpolationCheck.Enabled = frameMultiplierCombo.Items.Count > 0;
        UpdateFrameRateControls();
    }

    private void UpdateFrameRateControls()
    {
        var visible = frameInterpolationCheck.Checked &&
                      frameInterpolationCheck.Enabled &&
                      videoInfo?.IsImage == false;
        frameMultiplierCombo.Visible = visible;
        frameMultiplierCombo.Enabled = visible && exportCancellation is null;
        effectiveFpsValue.Visible = visible;
        UpdateEffectiveFps();
    }

    private void UpdateEffectiveFps()
    {
        if (videoInfo is null || frameMultiplierCombo.SelectedItem is not FrameMultiplierChoice choice)
        {
            effectiveFpsValue.Text = string.Empty;
            return;
        }

        effectiveFpsValue.Text = $"→ {videoInfo.FramesPerSecond * choice.Multiplier:0.###} fps effective";
    }

    private int GetFrameMultiplier() =>
        frameInterpolationCheck.Checked &&
        videoInfo?.IsImage == false &&
        frameMultiplierCombo.SelectedItem is FrameMultiplierChoice choice
            ? choice.Multiplier
            : 1;

    private void ConfigureMediaRange()
    {
        if (videoInfo is null) return;
        if (videoInfo.IsImage)
        {
            rangeStartText.Text = "—";
            rangeEndText.Text = "—";
            rangeStartText.Enabled = false;
            rangeEndText.Enabled = false;
            resetRangeButton.Enabled = false;
            rangeSummary.Text = "Still image";
            return;
        }

        ResetMediaRange();
        rangeStartText.Enabled = true;
        rangeEndText.Enabled = true;
        resetRangeButton.Enabled = true;
    }

    private void ResetMediaRange()
    {
        if (videoInfo?.IsImage != false) return;
        rangeStartText.Text = "00:00:00";
        rangeEndText.Text = FormatRangeTime(videoInfo.Duration);
        rangeSummary.Text = $"of {FormatRangeTime(videoInfo.Duration)}";
        UpdateOutputPath();
    }

    private bool TryGetMediaRange(out MediaRange range, out string error)
    {
        range = default;
        error = string.Empty;
        if (videoInfo is null)
        {
            error = "Choose a video first.";
            return false;
        }
        if (videoInfo.IsImage)
        {
            range = MediaRange.Full(videoInfo);
            return true;
        }
        if (!TryParseRangeTime(rangeStartText.Text, out var start))
        {
            error = "Start time must use seconds, MM:SS, or HH:MM:SS.";
            return false;
        }
        if (!TryParseRangeTime(rangeEndText.Text, out var end))
        {
            error = "End time must use seconds, MM:SS, or HH:MM:SS.";
            return false;
        }

        var frameTolerance = TimeSpan.FromSeconds(1d / Math.Max(1d, videoInfo.FramesPerSecond));
        if (Math.Abs((end - videoInfo.Duration).TotalMilliseconds) <= 1.1 ||
            end > videoInfo.Duration && end <= videoInfo.Duration + frameTolerance)
            end = videoInfo.Duration;
        if (start < TimeSpan.Zero || end <= start)
        {
            error = "End time must be later than start time.";
            return false;
        }
        if (start >= videoInfo.Duration)
        {
            error = "Start time must be before the end of the video.";
            return false;
        }
        if (end > videoInfo.Duration)
        {
            error = $"End time cannot exceed {FormatRangeTime(videoInfo.Duration)}.";
            return false;
        }

        range = new MediaRange(start, end);
        return true;
    }

    private bool IsCustomRange(MediaRange range) =>
        videoInfo?.IsImage == false &&
        (range.Start > TimeSpan.FromMilliseconds(1) ||
         range.End < videoInfo.Duration - TimeSpan.FromMilliseconds(1));

    private static bool TryParseRangeTime(string text, out TimeSpan value)
    {
        value = default;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3 ||
            !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0)
            return false;

        var hours = 0;
        var minutes = 0;
        if (parts.Length >= 2 &&
            (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out minutes) || minutes < 0))
            return false;
        if (parts.Length == 3 &&
            (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0))
            return false;
        if ((parts.Length >= 2 && seconds >= 60) ||
            (parts.Length == 3 && minutes >= 60))
            return false;

        var totalSeconds = hours * 3600d + minutes * 60d + seconds;
        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds) return false;
        value = TimeSpan.FromSeconds(totalSeconds);
        return true;
    }

    private static string FormatRangeTime(TimeSpan value)
    {
        var rounded = TimeSpan.FromMilliseconds(Math.Round(value.TotalMilliseconds));
        var hours = (long)Math.Floor(rounded.TotalHours);
        var baseText = $"{hours:00}:{rounded.Minutes:00}:{rounded.Seconds:00}";
        return rounded.Milliseconds == 0 ? baseText : $"{baseText}.{rounded.Milliseconds:000}";
    }

    private static void AddRangeLabel(TableLayoutPanel panel, int column, string text)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8F),
            Margin = new Padding(column == 1 ? 0 : 8, 2, 4, 0)
        }, column, 0);
    }

    private static void StyleRangeTextBox(TextBox textBox, string accessibleName)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.BackColor = Color.FromArgb(30, 36, 44);
        textBox.ForeColor = Color.White;
        textBox.TextAlign = HorizontalAlignment.Center;
        textBox.Margin = new Padding(0);
        textBox.AccessibleName = accessibleName;
    }

    private static void StyleOptionValue(Label label)
    {
        label.Dock = DockStyle.Fill;
        label.AutoEllipsis = true;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = Color.White;
        label.Margin = new Padding(0, 3, 7, 0);
    }

    private static void StyleComboBox(ComboBox combo)
    {
        combo.Dock = DockStyle.Fill;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Color.FromArgb(30, 36, 44);
        combo.ForeColor = Color.White;
        combo.Margin = new Padding(7, 3, 7, 0);
    }

    private static void StyleSecondaryButton(Button button, string text)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Padding = new Padding(10, 5, 10, 5);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = PanelColor;
        button.ForeColor = Color.White;
        button.Cursor = Cursors.Hand;
    }

    private static void AddDiagnostic(TableLayoutPanel panel, int column, string name, Label value)
    {
        panel.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8F),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(column == 0 ? 0 : 18, 4, 8, 0)
        }, column, 0);
        value.Text = "Checking…";
        value.AutoEllipsis = true;
        value.Dock = DockStyle.Fill;
        value.ForeColor = Color.White;
        value.TextAlign = ContentAlignment.MiddleLeft;
        panel.Controls.Add(value, column + 1, 0);
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }
        return $"{value:0.#} {suffixes[suffix]}";
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");

    private sealed record FrameMultiplierChoice(int Multiplier)
    {
        public override string ToString() => $"{Multiplier}×";
    }

    private sealed class DropPanel : Panel
    {
        public DropPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        public Color BorderColor { get; set; } = Color.White;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(BorderColor, 2F) { DashStyle = DashStyle.Dash };
            var rectangle = ClientRectangle;
            rectangle.Inflate(-2, -2);
            using var path = new GraphicsPath();
            const int radius = 14;
            var diameter = radius * 2;
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            e.Graphics.DrawPath(pen, path);
        }
    }
}
