using Microsoft.Win32;

using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace RemoveWhiteSpaces;

public partial class MainWindow : Window
{
    // ─────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────

    private AppSettings _settings = new();

    // Full unbroken output (all text, regardless of chunking).
    private string _fullOutput = string.Empty;

    // Current chunk list (count == 1 when chunking is off).
    private List<string> _chunks = new();
    private int _chunkIndex = 0;

    // ─────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySettings();

        // Ctrl+Enter → Convert from anywhere in the window.
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                RunConversion();
                e.Handled = true;
            }
        };

        // Hook auto-convert-on-paste via DataObject (fires before TextChanged).
        DataObject.AddPastingHandler(TxtInput, OnInputPasting);
    }

    // ─────────────────────────────────────────────────────────────────
    // Settings: load → apply to UI  /  read UI → save
    // ─────────────────────────────────────────────────────────────────

    private void ApplySettings()
    {
        // Window geometry — clamp to screen so we don't open off-screen.
        double sw = SystemParameters.VirtualScreenWidth;
        double sh = SystemParameters.VirtualScreenHeight;
        Left = Math.Max(0, Math.Min(_settings.WindowLeft, sw - 200));
        Top = Math.Max(0, Math.Min(_settings.WindowTop, sh - 100));
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        Topmost = _settings.AlwaysOnTop;
        TglAlwaysOnTop.IsChecked = _settings.AlwaysOnTop;
        TglAutoConvert.IsChecked = _settings.AutoConvertOnPaste;

        ChkLineComments.IsChecked = _settings.StripLineComments;
        ChkBlockComments.IsChecked = _settings.StripBlockComments;
        ChkXmlDocComments.IsChecked = _settings.StripXmlDocComments;
        ChkBlankLines.IsChecked = _settings.StripBlankLines;
        ChkCollapseToOneLine.IsChecked = _settings.CollapseToOneLine;

        TxtMaxTokens.Text = _settings.MaxTokensPerChunk.ToString();

        LstPatterns.Items.Clear();
        foreach (string p in _settings.CustomPatterns)
            LstPatterns.Items.Add(p);
    }

    private void CollectSettings()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;

        _settings.AlwaysOnTop = TglAlwaysOnTop.IsChecked == true;
        _settings.AutoConvertOnPaste = TglAutoConvert.IsChecked == true;

        _settings.StripLineComments = ChkLineComments.IsChecked == true;
        _settings.StripBlockComments = ChkBlockComments.IsChecked == true;
        _settings.StripXmlDocComments = ChkXmlDocComments.IsChecked == true;
        _settings.StripBlankLines = ChkBlankLines.IsChecked == true;
        _settings.CollapseToOneLine = ChkCollapseToOneLine.IsChecked == true;

        _settings.MaxTokensPerChunk = ParseMaxTokens();

        _settings.CustomPatterns.Clear();
        foreach (object item in LstPatterns.Items)
            _settings.CustomPatterns.Add(item.ToString()!);
    }

    // ─────────────────────────────────────────────────────────────────
    // Window events
    // ─────────────────────────────────────────────────────────────────

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        CollectSettings();
        _settings.Save();
    }

    private void TglAlwaysOnTop_Changed(object sender, RoutedEventArgs e)
        => Topmost = TglAlwaysOnTop.IsChecked == true;

    // ─────────────────────────────────────────────────────────────────
    // Input pane events
    // ─────────────────────────────────────────────────────────────────

    private void TxtInput_TextChanged(object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateInputStats();
        // Show/hide placeholder hint.
        TxtInputHint.Visibility =
            string.IsNullOrEmpty(TxtInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // Auto-convert on paste — fires before text is inserted.
    private void OnInputPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (TglAutoConvert.IsChecked == true)
        {
            // Schedule conversion after the paste actually lands in the TextBox.
            Dispatcher.BeginInvoke(
                new Action(RunConversion),
                System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // ── Drag-and-drop ────────────────────────────────────────────────

    private void TxtInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;   // Prevent TextBox from handling the drag itself.
    }

    private void TxtInput_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            LoadFiles(paths);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Button handlers
    // ─────────────────────────────────────────────────────────────────

    private void BtnConvert_Click(object sender, RoutedEventArgs e) => RunConversion();
    private void BtnClearInput_Click(object sender, RoutedEventArgs e) => ClearAll();
    private void BtnCopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveOutput();

    private void BtnPaste_Click(object sender, RoutedEventArgs e)
    {
        TxtInput.Focus();
        TxtInput.Paste();
    }

    private void BtnOpenFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open source file(s)",
            Filter = "Code & text files|*.cs;*.txt;*.vb;*.cpp;*.c;*.h;*.py;*.ts;*.js;*.json|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
            LoadFiles(dlg.FileNames);
    }

    // ── Chunk navigation ─────────────────────────────────────────────

    private void BtnPrevChunk_Click(object sender, RoutedEventArgs e)
    {
        if (_chunkIndex > 0) ShowChunk(_chunkIndex - 1);
    }

    private void BtnNextChunk_Click(object sender, RoutedEventArgs e)
    {
        if (_chunkIndex < _chunks.Count - 1) ShowChunk(_chunkIndex + 1);
    }

    private void BtnCopyChunk_Click(object sender, RoutedEventArgs e)
    {
        if (_chunks.Count == 0) return;
        Clipboard.SetText(_chunks[_chunkIndex], TextDataFormat.UnicodeText);
        SetStatus($"Chunk {_chunkIndex + 1} of {_chunks.Count} copied to clipboard.");
    }

    // ── Custom patterns ──────────────────────────────────────────────

    private void BtnAddPattern_Click(object sender, RoutedEventArgs e)
        => TryAddPattern();

    private void TxtNewPattern_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryAddPattern();
    }

    private void TryAddPattern()
    {
        string pat = TxtNewPattern.Text.Trim();
        if (string.IsNullOrEmpty(pat)) return;

        // Validate the regex before accepting it.
        try { _ = new Regex(pat); }
        catch (ArgumentException ex)
        {
            SetStatus($"Invalid regex: {ex.Message}", warning: true);
            return;
        }

        LstPatterns.Items.Add(pat);
        TxtNewPattern.Clear();
        SetStatus($"Pattern added: {pat}");
    }

    private void BtnRemovePattern_Click(object sender, RoutedEventArgs e)
    {
        if (LstPatterns.SelectedItem != null)
        {
            string removed = LstPatterns.SelectedItem.ToString()!;
            LstPatterns.Items.Remove(LstPatterns.SelectedItem);
            SetStatus($"Pattern removed: {removed}");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // File loading
    // ─────────────────────────────────────────────────────────────────

    private void LoadFiles(string[] paths)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (string path in paths)
            {
                if (paths.Length > 1)
                {
                    // Separator header so the AI knows where each file begins.
                    sb.AppendLine($"// ═══ {System.IO.Path.GetFileName(path)} ═══");
                }
                sb.AppendLine(System.IO.File.ReadAllText(path, Encoding.UTF8));
                if (paths.Length > 1) sb.AppendLine();
            }
            TxtInput.Text = sb.ToString();
            SetStatus(paths.Length == 1
                ? $"Loaded: {System.IO.Path.GetFileName(paths[0])}"
                : $"Loaded {paths.Length} files, concatenated with separators.");

            if (TglAutoConvert.IsChecked == true) RunConversion();
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading file(s): {ex.Message}", warning: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Core conversion pipeline
    // ─────────────────────────────────────────────────────────────────

    private void RunConversion()
    {
        string input = TxtInput.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            SetStatus("Nothing to convert — paste some code in the left pane first.", warning: true);
            return;
        }

        try
        {
            string compressed = Compress(input);
            _fullOutput = compressed;

            // Chunk if requested.
            int maxTokens = ParseMaxTokens();
            _chunks = maxTokens > 0
                ? SplitIntoChunks(compressed, maxTokens)
                : new List<string> { compressed };
            _chunkIndex = 0;

            ShowChunk(0);
            UpdateOutputStats(input, compressed);

            // Show / hide chunk nav panel.
            PnlChunkNav.Visibility = _chunks.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;

            SetStatus(_chunks.Count > 1
                ? $"Conversion complete — split into {_chunks.Count} chunks. Navigate with ◀ ▶ or use Copy All."
                : "Conversion complete.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error during conversion: {ex.Message}", warning: true);
        }
    }

    private string Compress(string input)
    {
        string text = input;

        // 1. Block comments  /* … */
        if (ChkBlockComments.IsChecked == true)
            text = RemoveBlockComments(text);

        // 2. XML doc comments  ///  (must precede the // pass)
        if (ChkXmlDocComments.IsChecked == true)
            text = RemoveXmlDocComments(text);

        // 3. Line comments  //
        if (ChkLineComments.IsChecked == true)
            text = RemoveLineComments(text);

        // 4. Custom regex patterns
        foreach (object item in LstPatterns.Items)
        {
            try
            {
                text = Regex.Replace(text, item.ToString()!, string.Empty,
                    RegexOptions.Multiline);
            }
            catch { /* Skip a broken pattern rather than crashing. */ }
        }

        // 5. Split, trim, and optionally drop blank lines.
        string[] rawLines = text.Split('\n');
        var kept = new List<string>(rawLines.Length);
        foreach (string raw in rawLines)
        {
            string trimmed = raw.TrimEnd('\r').Trim();
            if (ChkBlankLines.IsChecked == true && trimmed.Length == 0)
                continue;
            if (trimmed.Length > 0)
                kept.Add(trimmed);
        }

        // 6. Join.
        if (ChkCollapseToOneLine.IsChecked == true)
        {
            string joined = string.Join(" ", kept);
            // Collapse accidental double-spaces left by comment removal.
            joined = Regex.Replace(joined, @"  +", " ").Trim();
            return joined;
        }
        else
        {
            return string.Join(Environment.NewLine, kept);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Comment removal (state-machine, string-literal aware)
    // ─────────────────────────────────────────────────────────────────

    private static string RemoveBlockComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            // Verbatim string  @"…"
            if (i < src.Length - 1 && src[i] == '@' && src[i + 1] == '"')
            {
                sb.Append(src[i++]); sb.Append(src[i++]);
                while (i < src.Length)
                {
                    if (src[i] == '"')
                    {
                        sb.Append(src[i++]);
                        if (i < src.Length && src[i] == '"') sb.Append(src[i++]);
                        else break;
                    }
                    else sb.Append(src[i++]);
                }
                continue;
            }
            // Regular string  "…"
            if (src[i] == '"')
            {
                sb.Append(src[i++]);
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\') sb.Append(src[i++]);
                    if (i < src.Length) sb.Append(src[i++]);
                }
                if (i < src.Length) sb.Append(src[i++]);
                continue;
            }
            // Char literal  '.'
            if (src[i] == '\'')
            {
                sb.Append(src[i++]);
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\\') sb.Append(src[i++]);
                    if (i < src.Length) sb.Append(src[i++]);
                }
                if (i < src.Length) sb.Append(src[i++]);
                continue;
            }
            // Block comment  /*  …  */
            if (i < src.Length - 1 && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i < src.Length - 1 && !(src[i] == '*' && src[i + 1] == '/'))
                    i++;
                i += 2;
                sb.Append(' ');   // Preserve token separation.
                continue;
            }
            sb.Append(src[i++]);
        }
        return sb.ToString();
    }

    private static string RemoveXmlDocComments(string src)
        => Regex.Replace(src, @"[ \t]*///[^\n]*", string.Empty);

    private static string RemoveLineComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;
        while (i < src.Length)
        {
            // Verbatim string
            if (i < src.Length - 1 && src[i] == '@' && src[i + 1] == '"')
            {
                sb.Append(src[i++]); sb.Append(src[i++]);
                while (i < src.Length)
                {
                    if (src[i] == '"')
                    {
                        sb.Append(src[i++]);
                        if (i < src.Length && src[i] == '"') sb.Append(src[i++]);
                        else break;
                    }
                    else sb.Append(src[i++]);
                }
                continue;
            }
            // Regular string
            if (src[i] == '"')
            {
                sb.Append(src[i++]);
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\') sb.Append(src[i++]);
                    if (i < src.Length) sb.Append(src[i++]);
                }
                if (i < src.Length) sb.Append(src[i++]);
                continue;
            }
            // Char literal
            if (src[i] == '\'')
            {
                sb.Append(src[i++]);
                while (i < src.Length && src[i] != '\'')
                {
                    if (src[i] == '\\') sb.Append(src[i++]);
                    if (i < src.Length) sb.Append(src[i++]);
                }
                if (i < src.Length) sb.Append(src[i++]);
                continue;
            }
            // Line comment  //
            if (i < src.Length - 1 && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            sb.Append(src[i++]);
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // Chunk splitter
    // ─────────────────────────────────────────────────────────────────

    private static List<string> SplitIntoChunks(string text, int maxTokens)
    {
        // Rough conversion: 1 token ≈ 4 characters (works for English/code).
        int maxChars = maxTokens * 4;
        var chunks = new List<string>();

        if (text.Length <= maxChars)
        {
            chunks.Add(text);
            return chunks;
        }

        int start = 0;
        while (start < text.Length)
        {
            if (start + maxChars >= text.Length)
            {
                chunks.Add(text.Substring(start).Trim());
                break;
            }

            // Prefer a split at the last space within the window so we don't
            // cut in the middle of an identifier or keyword.
            int end = start + maxChars;
            int scanBack = Math.Min(100, end - start);   // look back up to 100 chars
            int splitAt = text.LastIndexOf(' ', end, scanBack);
            if (splitAt <= start) splitAt = end;         // No space found → hard cut.

            chunks.Add(text.Substring(start, splitAt - start).Trim());
            start = splitAt + 1;
        }

        return chunks;
    }

    private void ShowChunk(int index)
    {
        if (_chunks.Count == 0) return;
        index = Math.Max(0, Math.Min(index, _chunks.Count - 1));
        _chunkIndex = index;

        TxtOutput.Text = _chunks[index];

        if (_chunks.Count > 1)
        {
            TxtChunkInfo.Text = $"Part {index + 1} of {_chunks.Count}";
            int chunkTokens = _chunks[index].Length / 4;
            TxtChunkTokenHint.Text = $"~{chunkTokens:N0} tokens in this chunk";
            BtnPrevChunk.IsEnabled = index > 0;
            BtnNextChunk.IsEnabled = index < _chunks.Count - 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Output actions
    // ─────────────────────────────────────────────────────────────────

    private void CopyAll()
    {
        if (string.IsNullOrEmpty(_fullOutput))
        {
            SetStatus("Nothing to copy — run Convert first.", warning: true);
            return;
        }
        Clipboard.SetText(_fullOutput, TextDataFormat.UnicodeText);
        SetStatus(_chunks.Count > 1
            ? $"All {_chunks.Count} chunks copied to clipboard as one block."
            : "Output copied to clipboard.");
    }

    private void SaveOutput()
    {
        if (string.IsNullOrEmpty(_fullOutput))
        {
            SetStatus("Nothing to save — run Convert first.", warning: true);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Save compressed output",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".txt",
            FileName = "compressed_code"
        };

        if (dlg.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dlg.FileName, _fullOutput,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"Saved → {dlg.FileName}");
        }
    }

    private void ClearAll()
    {
        TxtInput.Clear();
        TxtOutput.Clear();
        _fullOutput = string.Empty;
        _chunks.Clear();
        PnlChunkNav.Visibility = Visibility.Collapsed;
        UpdateInputStats();
        ResetOutputStats();
        SetStatus("Cleared.");
    }

    // ─────────────────────────────────────────────────────────────────
    // UI stat helpers
    // ─────────────────────────────────────────────────────────────────

    private void UpdateInputStats()
    {
        string t = TxtInput.Text;
        int lines = string.IsNullOrEmpty(t) ? 0 : t.Split('\n').Length;
        TxtInputLines.Text = $"{lines:N0} lines";
        TxtInputChars.Text = $"{t.Length:N0} chars";
    }

    private void UpdateOutputStats(string input, string output)
    {
        int lines = string.IsNullOrEmpty(output) ? 0 : output.Split('\n').Length;
        TxtOutputLines.Text = $"{lines:N0} lines";
        TxtOutputChars.Text = $"{output.Length:N0} chars";
        TxtOutputTokens.Text = $"~{output.Length / 4:N0} tokens";

        if (input.Length > 0)
        {
            double pct = 100.0 * (1.0 - (double)output.Length / input.Length);
            TxtSavings.Text = $"▼ {pct:F1}% smaller";
        }
        else
        {
            TxtSavings.Text = "—";
        }
    }

    private void ResetOutputStats()
    {
        TxtOutputLines.Text = "0 lines";
        TxtOutputChars.Text = "0 chars";
        TxtOutputTokens.Text = "~0 tokens";
        TxtSavings.Text = "—";
    }

    private void SetStatus(string message, bool warning = false)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = warning
            ? new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8))   // red
            : new SolidColorBrush(Color.FromRgb(0x58, 0x5B, 0x70));  // muted
    }

    // ─────────────────────────────────────────────────────────────────
    // Utility
    // ─────────────────────────────────────────────────────────────────

    private int ParseMaxTokens()
    {
        if (int.TryParse(TxtMaxTokens.Text.Trim(), out int v) && v >= 0)
            return v;
        return 0;
    }
}