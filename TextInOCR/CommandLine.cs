using System.Text;

namespace TextInOCR
{
    internal enum OutputFormat { Markdown, Json, Excel }

    internal enum ParamKind { Choice, Int, Text, Flag }

    /// <summary>A pdf_to_markdown query parameter, exposed as a --kebab-case switch.</summary>
    internal sealed record ApiParam(string Name, ParamKind Kind, string Help, string[]? Choices = null, char? Short = null)
    {
        public string Switch => "--" + Name.Replace('_', '-');
    }

    internal sealed class CliException(string message) : Exception(message);

    internal sealed class CliOptions
    {
        public List<string> Inputs { get; } = [];
        public OutputFormat Format { get; set; } = OutputFormat.Markdown;
        public string? Output { get; set; }
        /// <summary>Per-file timeout; null scales it with the file size.</summary>
        public int? TimeoutSeconds { get; set; }
        public bool NoProxy { get; set; }
        public bool ShowHelp { get; set; }

        /// <summary>Query parameters set explicitly by the user, keyed by their TextIn name.</summary>
        public Dictionary<string, string> Query { get; } = [];
    }

    internal static class CommandLine
    {
        public static readonly ApiParam[] Params =
        [
            new("parse_mode", ParamKind.Choice,
                "Parsing mode (default: scan)\n" +
                "auto: widest coverage; scan: treat every page as an image\n" +
                "lite: text and tables only; parse: digital text layer only, fastest",
                ["auto", "scan", "lite", "parse"], 'm'),
            new("pdf_pwd", ParamKind.Text, "Password of an encrypted PDF"),
            new("page_start", ParamKind.Int, "First page to parse"),
            new("page_count", ParamKind.Int, "Number of pages to parse (default and max: 1000)"),
            new("dpi", ParamKind.Choice, "PDF coordinate DPI (default: 144; dynamic in auto mode)", ["72", "144", "216"]),
            new("apply_document_tree", ParamKind.Flag, "Detect heading levels (default: on)"),
            new("table_flavor", ParamKind.Choice, "Table syntax in markdown (default: html); none = read tables as text",
                ["md", "html", "none"]),
            new("get_image", ParamKind.Choice,
                "Return images (default: none)\npage = page images, objects = images inside pages, both = both",
                ["none", "page", "objects", "both"]),
            new("image_output_type", ParamKind.Choice, "How images are returned (default: default = URL / image ID)",
                ["default", "base64str"]),
            new("apply_image_analysis", ParamKind.Flag, "Describe sub-images with an LLM, in markdown (default: off)"),
            new("paratext_mode", ParamKind.Choice, "Header/footer/in-image text (default: annotation = md comments)",
                ["none", "annotation", "body"]),
            new("formula_level", ParamKind.Choice,
                "Formulas as LaTeX (default: 0)\n0 = inline and display, 1 = display only, 2 = off", ["0", "1", "2"]),
            new("underline_level", ParamKind.Choice,
                "Underlines, scan mode only (default: 0)\n0 = off, 1 = empty underlines only, 2 = all", ["0", "1", "2"]),
            new("apply_merge", ParamKind.Flag, "Merge split paragraphs and tables (default: on)"),
            new("markdown_details", ParamKind.Flag, "Return result.detail (default: on; off for md/xlsx output unless set)"),
            new("page_details", ParamKind.Flag, "Return result.pages (default: on; off for md/xlsx output unless set)"),
            new("raw_ocr", ParamKind.Flag, "Return raw OCR lines with coordinates; needs page details (default: off)"),
            new("char_details", ParamKind.Flag, "Return per-character positions (default: off)"),
            new("catalog_details", ParamKind.Flag, "Return result.catalog (table of contents) (default: off)"),
            new("get_excel", ParamKind.Flag, "Return result.excel_base64 (default: off; implied by -f xlsx)"),
            new("crop_dewarp", ParamKind.Flag, "Crop edges and dewarp before parsing (default: off)"),
            new("remove_watermark", ParamKind.Flag, "Remove watermarks before parsing (default: off)"),
            new("apply_chart", ParamKind.Flag, "Recognize charts and output them as tables (default: off)"),
        ];

        public static string Extension(this OutputFormat format) => format switch
        {
            OutputFormat.Json => "json",
            OutputFormat.Excel => "xlsx",
            _ => "md",
        };

        public static string DisplayName(this OutputFormat format) => format switch
        {
            OutputFormat.Json => "JSON (.json)",
            OutputFormat.Excel => "Excel (.xlsx)",
            _ => "Markdown (.md)",
        };

        public static CliOptions Parse(string[] args)
        {
            var opts = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--")
                {
                    opts.Inputs.AddRange(args.Skip(i + 1));
                    break;
                }
                if (arg.Length < 2 || arg[0] != '-')
                {
                    opts.Inputs.Add(arg);
                    continue;
                }

                string name = arg;
                string? inline = null;
                int eq = arg.IndexOf('=');
                if (arg.StartsWith("--") && eq > 0)
                {
                    name = arg[..eq];
                    inline = arg[(eq + 1)..];
                }

                string Value()
                {
                    if (inline != null) return inline;
                    if (i + 1 >= args.Length) throw new CliException($"Missing value for {name}.");
                    return args[++i];
                }

                switch (name)
                {
                    case "-h" or "-?" or "--help":
                        opts.ShowHelp = true;
                        continue;
                    case "-f" or "--format":
                        opts.Format = ParseFormat(Value());
                        continue;
                    case "-o" or "--output":
                        opts.Output = Value();
                        continue;
                    case "--timeout":
                        opts.TimeoutSeconds = ParseInt(name, Value(), min: 1);
                        continue;
                    case "--no-proxy":
                        opts.NoProxy = true;
                        continue;
                    case "--param":
                        string pair = Value();
                        int sep = pair.IndexOf('=');
                        if (sep <= 0) throw new CliException($"--param expects key=value, got '{pair}'.");
                        opts.Query[pair[..sep]] = pair[(sep + 1)..];
                        continue;
                }

                bool negated = name.StartsWith("--no-");
                ApiParam p = Params.FirstOrDefault(x => x.Switch == name || (x.Short is char c && name == $"-{c}"))
                    ?? (negated ? Params.FirstOrDefault(x => x.Kind == ParamKind.Flag && x.Switch == "--" + name[5..]) : null)
                    ?? throw new CliException($"Unknown option '{arg}'. Run TextInOCR --help for usage.");

                opts.Query[p.Name] = p.Kind switch
                {
                    // Flags never consume the next token, so `--crop-dewarp file.pdf` keeps file.pdf as an input.
                    ParamKind.Flag when negated && inline != null => throw new CliException($"{name} does not take a value."),
                    ParamKind.Flag when negated => "0",
                    ParamKind.Flag => inline == null ? "1" : ParseBool(name, inline),
                    ParamKind.Choice => ParseChoice(p, Value()),
                    ParamKind.Int => ParseInt(name, Value(), min: 0).ToString(),
                    _ => Value(),
                };
            }
            return opts;
        }

        static OutputFormat ParseFormat(string value) => value.ToLowerInvariant() switch
        {
            "md" or "markdown" => OutputFormat.Markdown,
            "json" => OutputFormat.Json,
            "xlsx" or "excel" => OutputFormat.Excel,
            _ => throw new CliException($"Unknown format '{value}'. Expected md, json or xlsx."),
        };

        static string ParseChoice(ApiParam p, string value) =>
            p.Choices!.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"Invalid value '{value}' for {p.Switch}. Expected one of: {string.Join(", ", p.Choices!)}.");

        static int ParseInt(string name, string value, int min) =>
            int.TryParse(value, out int n) && n >= min
                ? n
                : throw new CliException($"Invalid value '{value}' for {name}. Expected an integer >= {min}.");

        static string ParseBool(string name, string value) => value.ToLowerInvariant() switch
        {
            "1" or "true" => "1",
            "0" or "false" => "0",
            _ => throw new CliException($"Invalid value '{value}' for {name}. Expected 0, 1, true or false."),
        };

        public static string HelpText()
        {
            List<(string, string)> tool =
            [
                ("-f, --format <md|json|xlsx>",
                    "Output format (default: md)\nmd = result.markdown, json = full response, xlsx = result.excel_base64"),
                ("-o, --output <path>", "Output file, existing directory, or - for stdout\n(default: .\\<BaseName>.<ext>)"),
                ("    --timeout <seconds>", "Per-file timeout for upload + parsing\n(default: 600 + twice the upload time at 60 KB/s)"),
                ("    --no-proxy", "Connect directly, ignoring the system proxy and HTTP(S)_PROXY variables"),
                ("-h, --help", "Show this help"),
            ];
            var api = Params.Select(p => (
                (p.Short is char c ? $"-{c}, " : "    ")
                    + (p.Kind == ParamKind.Flag ? "--[no-]" + p.Switch[2..] : p.Switch)
                    + p.Kind switch
                    {
                        ParamKind.Choice => $" <{string.Join('|', p.Choices!)}>",
                        ParamKind.Int => " <n>",
                        ParamKind.Text => " <text>",
                        _ => "",
                    },
                p.Help)).ToList();
            api.Add(("    --param <key=value>", "Send any other query parameter as-is (repeatable)"));

            int width = tool.Concat(api).Max(r => r.Item1.Length) + 3;
            var sb = new StringBuilder();
            sb.AppendLine("TextInOCR - TextIn xParse document parsing (pdf_to_markdown) from the command line");
            sb.AppendLine();
            sb.AppendLine("Usage: TextInOCR <file|url>... [options]");
            sb.AppendLine();
            sb.AppendLine("Options:");
            AppendRows(sb, tool, width);
            sb.AppendLine();
            sb.AppendLine("TextIn parameters (unset ones use the server default; --[no-]name also accepts --name=0|1):");
            AppendRows(sb, api, width);
            sb.AppendLine();
            sb.AppendLine("Examples:");
            sb.AppendLine("  TextInOCR scan.pdf                           writes .\\scan.md");
            sb.AppendLine("  TextInOCR a.pdf b.png -f json -o out\\        writes out\\a.json and out\\b.json");
            sb.AppendLine("  TextInOCR scan.pdf -m auto --table-flavor md --page-start 2 --page-count 5 -o -");
            sb.AppendLine();
            sb.AppendLine("Credentials are read from the TextIn-x-ti-app-id and TextIn-x-ti-secret-code environment variables.");
            sb.AppendLine("API docs: https://docs.textin.com/xparse/parse-quickstart");
            return sb.ToString();
        }

        static void AppendRows(StringBuilder sb, IEnumerable<(string Left, string Right)> rows, int width)
        {
            foreach (var (left, right) in rows)
            {
                string[] lines = right.Split('\n');
                sb.Append("  ").Append(left.PadRight(width)).AppendLine(lines[0]);
                foreach (string line in lines.Skip(1))
                    sb.Append(' ', width + 2).AppendLine(line);
            }
        }
    }
}
