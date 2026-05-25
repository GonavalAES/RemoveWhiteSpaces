using Microsoft.Win32;

using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace RemoveWhiteSpaces;

public partial class MainWindow : Window
{
    // ─────────────────────────────────────────────────────────────────
    // Construction
    // ─────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        // Allow Ctrl+Enter to trigger Convert from anywhere in the window.
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                RunConversion();
                e.Handled = true;
            }
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Event handlers
    // ─────────────────────────────────────────────────────────────────

    private void BtnConvert_Click(object sender, RoutedEventArgs e) => RunConversion();
    private void BtnClearInput_Click(object sender, RoutedEventArgs e) => ClearAll();
    private void BtnCopy_Click(object sender, RoutedEventArgs e) => CopyOutput();
    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveOutput();

    private void TxtInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateInputStats();

    // ─────────────────────────────────────────────────────────────────
    // Core conversion
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
            string result = Compress(input);
            TxtOutput.Text = result;
            UpdateOutputStats(input, result);
            SetStatus("Conversion complete.  Ctrl+C or use the Copy button to grab the output.");
        }
        catch (Exception ex)
        {
            SetStatus($"Error during conversion: {ex.Message}", warning: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Compression algorithm
    //
    // Pass order matters:
    //   1. Block comments  /* … */   (may span multiple lines)
    //   2. XML doc comments  /// …   (before line-comment pass)
    //   3. Line comments   // …      (single line)
    //   4. Per-line trim and blank-line removal
    //   5. Join / collapse to one line
    // ─────────────────────────────────────────────────────────────────

    private string Compress(string input)
    {
        string text = input;

        // 1. Block comments  /* ... */
        if (ChkBlockComments.IsChecked == true)
            text = RemoveBlockComments(text);

        // 2. XML doc comments  /// (must come before // pass)
        if (ChkXmlDocComments.IsChecked == true)
            text = RemoveXmlDocComments(text);

        // 3. Line comments  //
        if (ChkLineComments.IsChecked == true)
            text = RemoveLineComments(text);

        // 4. Split, trim lines, drop blank lines
        string[] rawLines = text.Split('\n');
        var kept = new System.Collections.Generic.List<string>(rawLines.Length);

        foreach (string raw in rawLines)
        {
            string trimmed = raw.TrimEnd('\r').Trim();   // normalise CRLF / LF

            if (ChkBlankLines.IsChecked == true && trimmed.Length == 0)
                continue;

            if (trimmed.Length > 0)
                kept.Add(trimmed);
        }

        // 5. Combine
        if (ChkCollapseToOneLine.IsChecked == true)
        {
            // Join with a single space, then collapse any accidental double-spaces
            // that can arise when a block-comment removal leaves trailing spaces.
            string joined = string.Join(" ", kept);
            joined = Regex.Replace(joined, @"  +", " ").Trim();
            return joined;
        }
        else
        {
            // Multi-line output: just clean lines, no single-line collapse.
            return string.Join(Environment.NewLine, kept);
        }
    }

    // ── Removal helpers ──────────────────────────────────────────────

    /// <summary>
    /// Removes /* … */ block comments, including multi-line ones,
    /// while leaving string literals that happen to contain /* intact.
    /// Uses a state-machine approach: tracks whether the parser is inside
    /// a string literal, a char literal, or a comment.
    /// </summary>
    private static string RemoveBlockComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;

        while (i < src.Length)
        {
            // Inside a verbatim string  @"..."
            if (i < src.Length - 1 && src[i] == '@' && src[i + 1] == '"')
            {
                sb.Append(src[i++]); // @
                sb.Append(src[i++]); // "
                while (i < src.Length)
                {
                    if (src[i] == '"')
                    {
                        sb.Append(src[i++]);
                        // Escaped quote "" inside verbatim string
                        if (i < src.Length && src[i] == '"')
                            sb.Append(src[i++]);
                        else
                            break;
                    }
                    else
                        sb.Append(src[i++]);
                }
                continue;
            }

            // Inside a regular string  "..."
            if (src[i] == '"')
            {
                sb.Append(src[i++]);
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\') sb.Append(src[i++]); // skip escape
                    if (i < src.Length) sb.Append(src[i++]);
                }
                if (i < src.Length) sb.Append(src[i++]); // closing "
                continue;
            }

            // Inside a char literal  '.'
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

            // Block comment start  /*
            if (i < src.Length - 1 && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2; // skip /*
                while (i < src.Length - 1 && !(src[i] == '*' && src[i + 1] == '/'))
                    i++;
                i += 2; // skip */
                        // Replace the whole comment with a single space so tokens don't merge.
                sb.Append(' ');
                continue;
            }

            sb.Append(src[i++]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Removes /// XML documentation comment lines entirely.
    /// Must run before the plain // pass.
    /// </summary>
    private static string RemoveXmlDocComments(string src)
        // Match optional leading whitespace, then ///, then rest of line.
        => Regex.Replace(src, @"[ \t]*///[^\n]*", string.Empty);

    /// <summary>
    /// Removes // line comments, but not those inside string literals.
    /// Uses a state-machine pass similar to the block-comment remover.
    /// </summary>
    private static string RemoveLineComments(string src)
    {
        var sb = new StringBuilder(src.Length);
        int i = 0;

        while (i < src.Length)
        {
            // Verbatim string
            if (i < src.Length - 1 && src[i] == '@' && src[i + 1] == '"')
            {
                sb.Append(src[i++]);
                sb.Append(src[i++]);
                while (i < src.Length)
                {
                    if (src[i] == '"')
                    {
                        sb.Append(src[i++]);
                        if (i < src.Length && src[i] == '"')
                            sb.Append(src[i++]);
                        else
                            break;
                    }
                    else
                        sb.Append(src[i++]);
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
                // Skip to end of line (keep the newline itself).
                while (i < src.Length && src[i] != '\n')
                    i++;
                continue;
            }

            sb.Append(src[i++]);
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────
    // UI helpers
    // ─────────────────────────────────────────────────────────────────

    private void ClearAll()
    {
        TxtInput.Clear();
        TxtOutput.Clear();
        UpdateInputStats();
        ResetOutputStats();
        SetStatus("Cleared.");
    }

    private void CopyOutput()
    {
        if (string.IsNullOrEmpty(TxtOutput.Text))
        {
            SetStatus("Nothing to copy — run Convert first.", warning: true);
            return;
        }
        Clipboard.SetText(TxtOutput.Text, TextDataFormat.UnicodeText);
        SetStatus("Output copied to clipboard.");
    }

    private void SaveOutput()
    {
        if (string.IsNullOrEmpty(TxtOutput.Text))
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
            System.IO.File.WriteAllText(dlg.FileName, TxtOutput.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus($"Saved → {dlg.FileName}");
        }
    }

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
        TxtSavings.Text = "—";
    }

    private void SetStatus(string message, bool warning = false)
    {
        TxtStatus.Text = message;
        TxtStatus.Foreground = warning
            ? new System.Windows.Media.SolidColorBrush(
                  System.Windows.Media.Color.FromRgb(0xF3, 0x8B, 0xA8))   // red-ish
            : new System.Windows.Media.SolidColorBrush(
                  System.Windows.Media.Color.FromRgb(0x58, 0x5B, 0x70));  // muted
    }
}