using System.Diagnostics;
using System.Text;

namespace VoLTEVendorPatcher;

internal sealed class MainForm : Form
{
    private static readonly Color WindowColor = Color.FromArgb(244, 247, 251);
    private static readonly Color BorderColor = Color.FromArgb(218, 224, 232);
    private static readonly Color TextColor = Color.FromArgb(27, 36, 49);
    private static readonly Color MutedColor = Color.FromArgb(91, 103, 119);
    private static readonly Color PrimaryColor = Color.FromArgb(31, 111, 235);
    private static readonly Color SuccessColor = Color.FromArgb(21, 128, 61);

    private readonly TextBox _input = new();
    private readonly TextBox _output = new();
    private readonly TextBox _details = new();
    private readonly RichTextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _browseInput = new();
    private readonly Button _browseOutput = new();
    private readonly Button _analyze = new();
    private readonly Button _patch = new();
    private readonly Button _open = new();
    private readonly Button _cancel = new();
    private readonly PatcherEngine _engine = new();
    private readonly string _logFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoLTEVendorPatcher", "logs",
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");

    private AnalysisResult? _analysis;
    private CancellationTokenSource? _operationCts;

    public MainForm()
    {
        SuspendLayout();

        Text = "VoLTE Vendor Patcher";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        ForeColor = TextColor;
        BackColor = WindowColor;
        ClientSize = new Size(920, 650);
        MinimumSize = new Size(760, 580);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        Controls.Add(BuildRootLayout());

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logFile)!);
        }
        catch
        {
            // Logging must never stop analysis or patching.
        }

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += (_, _) => _operationCts?.Cancel();

        ResumeLayout(performLayout: true);
        SetInputPathFromArgs();
    }

    private Control BuildRootLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 20),
            ColumnCount = 1,
            RowCount = 9,
            BackColor = WindowColor
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 14F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 14F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 16F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildPathCard(), 0, 2);
        root.Controls.Add(BuildActionRow(), 0, 4);

        _progress.Dock = DockStyle.Fill;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Style = ProgressBarStyle.Continuous;
        root.Controls.Add(_progress, 0, 6);
        root.Controls.Add(BuildContentArea(), 0, 8);
        return root;
    }

    private static Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "VoLTE Vendor Patcher",
            Font = new Font("Segoe UI", 22F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = TextColor,
            AutoSize = true,
            Margin = Padding.Empty
        };
        var subtitle = new Label
        {
            Text = "OPPO / Realme MediaTek  •  Android 8.1–10  •  Không sửa image gốc",
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedColor,
            AutoSize = true,
            Margin = new Padding(2, 3, 0, 0)
        };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        return header;
    }

    private Control BuildPathCard()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Top,
            Height = 143,
            Padding = new Padding(16, 14, 16, 12),
            Margin = Padding.Empty,
            BackColor = Color.White
        };
        var paths = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 4,
            Margin = Padding.Empty
        };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 12F));
        paths.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        paths.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        ConfigurePathBox(_input, readOnly: true);
        ConfigurePathBox(_output, readOnly: false);
        ConfigureSecondaryButton(_browseInput, "Chọn file");
        ConfigureSecondaryButton(_browseOutput, "Chọn thư mục lưu", 150);
        _browseInput.Click += BrowseInput;
        _browseOutput.Click += BrowseOutput;

        paths.Controls.Add(CreatePathLabel("Vendor image"), 0, 0);
        paths.Controls.Add(_input, 1, 0);
        paths.Controls.Add(_browseInput, 2, 0);
        paths.Controls.Add(CreatePathLabel("Output image"), 0, 2);
        paths.Controls.Add(_output, 1, 2);
        paths.Controls.Add(_browseOutput, 2, 2);

        var hint = new Label
        {
            Text = "Kéo và thả vendor.img hoặc file RAR chứa vendor image để bắt đầu.",
            ForeColor = MutedColor,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 0)
        };
        paths.Controls.Add(hint, 0, 3);
        paths.SetColumnSpan(hint, 3);
        card.Controls.Add(paths);
        return card;
    }

    private Control BuildActionRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left
        };

        ConfigurePrimaryButton(_analyze, "Phân tích", PrimaryColor, 116);
        ConfigurePrimaryButton(_patch, "Bắt đầu & Lưu", SuccessColor, 142);
        ConfigureSecondaryButton(_open, "Mở thư mục", 122);
        ConfigureSecondaryButton(_cancel, "Hủy", 82);
        _patch.Enabled = false;
        _open.Enabled = false;
        _cancel.Enabled = false;

        _analyze.Click += async (_, _) => await AnalyzeAsync();
        _patch.Click += async (_, _) => await PatchAsync();
        _open.Click += (_, _) => OpenOutputFolder();
        _cancel.Click += (_, _) => _operationCts?.Cancel();

        actions.Controls.Add(_analyze);
        actions.Controls.Add(_patch);
        actions.Controls.Add(_open);
        actions.Controls.Add(_cancel);

        _status.Text = "Chưa chọn image";
        _status.ForeColor = MutedColor;
        _status.BackColor = Color.FromArgb(232, 237, 244);
        _status.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        _status.AutoSize = true;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Padding = new Padding(12, 7, 12, 7);
        _status.Margin = Padding.Empty;
        _status.Anchor = AnchorStyles.Right;

        row.Controls.Add(actions, 0, 0);
        row.Controls.Add(_status, 1, 0);
        return row;
    }

    private Control BuildContentArea()
    {
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 14F));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 42F));

        _details.Multiline = true;
        _details.ReadOnly = true;
        _details.ScrollBars = ScrollBars.Vertical;
        _details.WordWrap = false;
        _details.BorderStyle = BorderStyle.None;
        _details.BackColor = Color.White;
        _details.ForeColor = TextColor;
        _details.Font = new Font("Consolas", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        _details.Dock = DockStyle.Fill;

        _log.ReadOnly = true;
        _log.BorderStyle = BorderStyle.None;
        _log.BackColor = Color.FromArgb(249, 250, 252);
        _log.ForeColor = TextColor;
        _log.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _log.WordWrap = false;
        _log.DetectUrls = false;
        _log.Dock = DockStyle.Fill;

        content.Controls.Add(BuildTextCard("Kết quả phân tích", _details), 0, 0);
        content.Controls.Add(BuildTextCard("Nhật ký hoạt động", _log), 0, 2);
        return content;
    }

    private static Control BuildTextCard(string titleText, Control body)
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 11, 14, 13),
            Margin = Padding.Empty,
            BackColor = Color.White
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.Controls.Add(new Label
        {
            Text = titleText,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = TextColor,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        }, 0, 0);
        layout.Controls.Add(body, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private static Label CreatePathLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        ForeColor = TextColor,
        Margin = Padding.Empty
    };

    private static void ConfigurePathBox(TextBox box, bool readOnly)
    {
        box.Dock = DockStyle.Fill;
        box.ReadOnly = readOnly;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.BackColor = readOnly ? Color.FromArgb(248, 250, 252) : Color.White;
        box.ForeColor = TextColor;
        box.Margin = new Padding(0, 2, 12, 3);
    }

    private static void ConfigurePrimaryButton(Button button, string text, Color color, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 36;
        button.BackColor = color;
        button.ForeColor = Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 10, 0);
        button.UseVisualStyleBackColor = false;
        button.UseMnemonic = false;
        button.EnabledChanged += (_, _) =>
        {
            button.BackColor = button.Enabled ? color : Color.FromArgb(226, 231, 238);
            button.ForeColor = button.Enabled ? Color.White : Color.FromArgb(126, 137, 151);
        };
    }

    private static void ConfigureSecondaryButton(Button button, string text, int width = 104)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 34;
        button.BackColor = Color.White;
        button.ForeColor = TextColor;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(190, 199, 211);
        button.FlatAppearance.BorderSize = 1;
        button.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 0, 10, 0);
        button.UseVisualStyleBackColor = false;
    }

    private void SetInputPathFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            SetInputPath(args[1]);
        }
    }

    private void BrowseInput(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Vendor image / RAR (*.img;*.simg;*.rar)|*.img;*.simg;*.rar|Android image (*.img;*.simg)|*.img;*.simg|RAR (*.rar)|*.rar|Tất cả tệp (*.*)|*.*",
            CheckFileExists = true,
            Title = "Chọn vendor image"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetInputPath(dialog.FileName);
        }
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Android image (*.img)|*.img|Tất cả tệp (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(_output.Text)
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = dialog.FileName;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            SetInputPath(files[0]);
        }
    }

    private void SetInputPath(string path)
    {
        _input.Text = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_input.Text) ?? Environment.CurrentDirectory;
        var stem = Path.GetFileNameWithoutExtension(_input.Text);
        _output.Text = Path.Combine(directory, stem + "_VoLTE_patched.img");
        _analysis = null;
        _patch.Enabled = false;
        _open.Enabled = false;
        _details.Clear();
        SetStatus("Sẵn sàng phân tích", StatusTone.Neutral);
        _ = AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_operationCts != null)
        {
            return;
        }

        var input = _input.Text.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            MessageBox.Show(this, "Hãy chọn một vendor image để phân tích.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Đang phân tích…");
        try
        {
            var progress = new Progress<ProgressUpdate>(SetProgress);
            _analysis = await _engine.AnalyzeAsync(input, progress, _operationCts.Token);
            _details.Text = _analysis.ToDisplayText();
            _patch.Enabled = _analysis.CanPatch;

            if (_analysis.State == PatchState.AlreadyPatched)
            {
                SetStatus("Đã patch VoLTE", StatusTone.Success);
            }
            else if (_analysis.CanPatch)
            {
                SetStatus("Có thể patch", StatusTone.Success);
            }
            else
            {
                SetStatus("Không tương thích", StatusTone.Warning);
            }
            AppendLog(_analysis.Message);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Đã hủy", StatusTone.Neutral);
            AppendLog("Đã hủy.");
        }
        catch (Exception ex)
        {
            _analysis = null;
            _patch.Enabled = false;
            SetStatus("Lỗi phân tích", StatusTone.Error);
            AppendLog("LỖI: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PatchAsync()
    {
        if (_analysis == null || !_analysis.CanPatch)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_output.Text))
        {
            MessageBox.Show(this, "Hãy chọn đường dẫn output.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(Path.GetFullPath(_input.Text), Path.GetFullPath(_output.Text),
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Output phải khác image gốc.", "Đường dẫn không hợp lệ",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (File.Exists(_output.Text))
        {
            MessageBox.Show(this,
                "Output đã tồn tại. Hãy chọn tên khác để không ghi đè image.",
                "Output đã tồn tại", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Đang patch…");
        try
        {
            var progress = new Progress<ProgressUpdate>(SetProgress);
            var result = await _engine.PatchAsync(
                _analysis, _output.Text, progress, _operationCts.Token);
            _details.Text = result.ToDisplayText();
            SetStatus("Patch thành công", StatusTone.Success);
            _open.Enabled = true;
            AppendLog(result.Message);
            MessageBox.Show(this, "Đã tạo image VoLTE:\n" + result.OutputPath,
                "Hoàn tất", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Đã hủy", StatusTone.Neutral);
            AppendLog("Đã hủy; image gốc không đổi.");
        }
        catch (Exception ex)
        {
            SetStatus("Patch thất bại", StatusTone.Error);
            AppendLog("LỖI: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Patch thất bại",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _browseInput.Enabled = !busy;
        _browseOutput.Enabled = !busy;
        _output.ReadOnly = busy;
        _analyze.Enabled = !busy;
        _patch.Enabled = !busy && _analysis?.CanPatch == true;
        _open.Enabled = !busy && File.Exists(_output.Text);
        _cancel.Enabled = busy;
        AllowDrop = !busy;
        SetStatus(status, StatusTone.Working);
        if (busy)
        {
            _progress.Value = 0;
        }
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        _browseInput.Enabled = true;
        _browseOutput.Enabled = true;
        _output.ReadOnly = false;
        _analyze.Enabled = true;
        _patch.Enabled = _analysis?.CanPatch == true;
        _cancel.Enabled = false;
        AllowDrop = true;
    }

    private void SetProgress(ProgressUpdate update)
    {
        SetStatus(update.Message, StatusTone.Working);
        _progress.Value = Math.Clamp(update.Percent, 0, 100);
        if (!string.IsNullOrWhiteSpace(update.Log))
        {
            AppendLog(update.Log!);
        }
    }

    private void SetStatus(string text, StatusTone tone)
    {
        _status.Text = text;
        (_status.ForeColor, _status.BackColor) = tone switch
        {
            StatusTone.Success => (Color.FromArgb(22, 101, 52), Color.FromArgb(220, 252, 231)),
            StatusTone.Warning => (Color.FromArgb(146, 64, 14), Color.FromArgb(255, 237, 213)),
            StatusTone.Error => (Color.FromArgb(153, 27, 27), Color.FromArgb(254, 226, 226)),
            StatusTone.Working => (Color.FromArgb(30, 64, 175), Color.FromArgb(219, 234, 254)),
            _ => (MutedColor, Color.FromArgb(232, 237, 244))
        };
    }

    private void AppendLog(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }

        var entry = $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}";
        _log.AppendText(entry);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
        try
        {
            File.AppendAllText(_logFile,
                $"[{DateTime.Now:O}] {text}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Logging must never stop analysis or patching.
        }
    }

    private void OpenOutputFolder()
    {
        if (!File.Exists(_output.Text))
        {
            return;
        }

        var startInfo = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("/select," + Path.GetFullPath(_output.Text));
        Process.Start(startInfo);
    }

    private enum StatusTone
    {
        Neutral,
        Working,
        Success,
        Warning,
        Error
    }
}

internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.UserPaint |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(218, 224, 232));
        var bounds = ClientRectangle;
        bounds.Width -= 1;
        bounds.Height -= 1;
        e.Graphics.DrawRectangle(pen, bounds);
    }
}

internal sealed record ProgressUpdate(int Percent, string Message, string? Log = null);
