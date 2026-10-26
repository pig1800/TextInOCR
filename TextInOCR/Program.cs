using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TextInOCR
{
    internal class Program
    {
        const string AppIdVar = "TextIn-x-ti-app-id";
        const string SecretCodeVar = "TextIn-x-ti-secret-code";

        // Default per-file timeout: a processing allowance plus twice the upload time at a slow 60 KB/s.
        const int ProcessingTimeoutSeconds = 600;
        const double SlowUploadBytesPerSecond = 60 * 1024;

        static readonly JsonSerializerOptions JsonOutputOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep CJK text readable
        };

        static async Task<int> Main(string[] args)
        {
            CliOptions opts;
            List<(string Input, string Output)> jobs;
            try
            {
                opts = CommandLine.Parse(args);
                if (opts.ShowHelp || opts.Inputs.Count == 0)
                {
                    Console.Write(CommandLine.HelpText());
                    return opts.ShowHelp ? 0 : 2;
                }
                jobs = PlanJobs(opts);
            }
            catch (CliException e)
            {
                Console.Error.WriteLine($"Error: {e.Message}");
                return 2;
            }

            string? appId = Environment.GetEnvironmentVariable(AppIdVar)?.Trim();
            string? secretCode = Environment.GetEnvironmentVariable(SecretCodeVar)?.Trim();
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(secretCode))
            {
                Console.Error.WriteLine($"Error: set the {AppIdVar} and {SecretCodeVar} environment variables " +
                    "(see https://www.textin.com/console/dashboard/setting).");
                return 2;
            }

            Dictionary<string, string> query = BuildQuery(opts);
            var log = new Reporter(toStderr: opts.Output == "-");
            using var client = new TextInClient(appId, secretCode, useProxy: !opts.NoProxy);
            int failures = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                var (input, output) = jobs[i];
                if (i > 0) log.Line();
                try
                {
                    await ProcessAsync(client, log, input, output, opts.Format, query, opts.TimeoutSeconds);
                }
                catch (Exception e)
                {
                    log.Line($"Error: {e.Message}");
                    failures++;
                }
            }
            if (jobs.Count > 1)
            {
                log.Line();
                log.Line($"Done: {jobs.Count - failures} succeeded, {failures} failed.");
            }
            return failures == 0 ? 0 : 1;
        }

        /// <summary>Resolves every output path up front, so bad combinations fail before any (billed) request.</summary>
        static List<(string Input, string Output)> PlanJobs(CliOptions opts)
        {
            string? outDir = null;
            if (opts.Output is string o && o != "-"
                && (Directory.Exists(o) || o.EndsWith(Path.DirectorySeparatorChar) || o.EndsWith(Path.AltDirectorySeparatorChar)))
                outDir = o;
            else if (opts.Output != null && opts.Inputs.Count > 1)
                throw new CliException("With multiple inputs, --output must be a directory.");

            var jobs = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string input in opts.Inputs)
            {
                bool isUrl = TextInClient.IsUrl(input);
                if (!isUrl && !File.Exists(input))
                    throw new CliException($"Input file not found: {input}");

                string output = opts.Output != null && outDir == null
                    ? opts.Output
                    : Path.Combine(outDir ?? "", $"{BaseName(input)}.{opts.Format.Extension()}");
                if (output != "-")
                {
                    string full = Path.GetFullPath(output);
                    if (!isUrl && full.Equals(Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                        throw new CliException($"Output would overwrite the input file {input}; choose another path with -o.");
                    if (!seen.Add(full))
                        throw new CliException($"More than one input would be written to {output}; convert them separately with -o.");
                }
                jobs.Add((input, output));
            }
            return jobs;
        }

        /// <summary>Equivalent of PowerShell's $_.BaseName; for URLs, taken from the last path segment.</summary>
        static string BaseName(string input)
        {
            if (!TextInClient.IsUrl(input))
                return Path.GetFileNameWithoutExtension(input);

            string name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(new Uri(input).AbsolutePath).TrimEnd('/'));
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Length > 0 ? name : "result";
        }

        static Dictionary<string, string> BuildQuery(CliOptions opts)
        {
            var query = new Dictionary<string, string>(opts.Query);
            if (opts.Format == OutputFormat.Excel)
                query["get_excel"] = "1";

            // md/xlsx only read result.markdown / result.excel_base64, so skip the bulky detail and pages
            // payloads unless asked for. lite mode returns `elements` instead; leave its request alone.
            if (opts.Format != OutputFormat.Json && query.GetValueOrDefault("parse_mode") != "lite")
            {
                query.TryAdd("markdown_details", "0");
                if (query.GetValueOrDefault("raw_ocr") != "1") // raw_ocr requires page_details
                    query.TryAdd("page_details", "0");
            }
            return query;
        }

        static async Task ProcessAsync(TextInClient client, Reporter log, string input, string output, OutputFormat format,
            IReadOnlyDictionary<string, string> query, int? timeoutSeconds)
        {
            bool isUrl = TextInClient.IsUrl(input);
            long size = isUrl ? 0 : new FileInfo(input).Length;
            var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? ProcessingTimeoutSeconds + 2 * size / SlowUploadBytesPerSecond);
            log.Line(isUrl ? $"Input URL:   {input}" : $"Input file:  {input} ({Reporter.FormatSize(size)})");
            log.Line($"Output type: {format.DisplayName()}");
            log.Line($"Output file: {(output == "-" ? "stdout" : output)}");
            log.Line("Parameters:  " + (query.Count == 0
                ? "(server defaults)"
                : string.Join(", ", query.Select(kv => $"{kv.Key}={(kv.Key == "pdf_pwd" ? "***" : kv.Value)}"))));
            log.Line($"Proxy:       {client.Proxy}");
            log.Line($"Timeout:     {timeout.TotalSeconds:0}s{(timeoutSeconds == null ? " (auto)" : "")}");

            log.Line(isUrl ? "Sending URL..." : "Uploading...");
            var phase = Stopwatch.StartNew(); // upload, then restarted for the server's processing + reply
            var uploadClock = new Stopwatch();
            var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnUpload(long sent)
            {
                uploadClock.Start();
                bool done = sent >= size;
                double speed = sent / Math.Max(uploadClock.Elapsed.TotalSeconds, 0.001);
                log.Status($"{Reporter.FormatSize(sent)} / {Reporter.FormatSize(size)}, {Reporter.FormatSize(speed)}/s", done);
                if (done) uploaded.TrySetResult();
            }

            Task<JsonObject> request = client.ParseAsync(input, query, timeout, isUrl ? null : OnUpload);
            if (!isUrl)
            {
                await Task.WhenAny(uploaded.Task, request);
                if (uploaded.Task.IsCompleted) // otherwise it failed mid-upload; awaiting it below throws
                {
                    log.Line($"Upload completed in {Reporter.FormatDuration(phase.Elapsed)}.");
                    phase.Restart();
                }
            }
            if (!request.IsCompleted)
            {
                const string waiting = "Waiting for reply...";
                if (!log.Interactive) log.Line(waiting);
                do log.Status($"{waiting} {(int)phase.Elapsed.TotalSeconds}s");
                while (await Task.WhenAny(request, Task.Delay(1000)) != request);
                log.EndStatus();
            }
            JsonObject response = await request;
            log.Line($"Reply received after {Reporter.FormatDuration(phase.Elapsed)}.");
            JsonObject result = response["result"] as JsonObject ?? throw new TextInException("Response has no result.");

            if (result["valid_page_number"] is JsonNode valid)
                log.Line($"Successfully recognized {valid} / {result["total_page_number"]} pages.");
            else if (result["success_count"] is JsonNode count)
                log.Line($"Successfully recognized {count} pages.");
            if (response["code"]?.GetValue<int>() == TextInClient.PartialSuccessCode)
                log.Line($"Warning: some pages failed to parse ({response["message"]}).");

            byte[] bytes = format switch
            {
                OutputFormat.Json => Encoding.UTF8.GetBytes(response.ToJsonString(JsonOutputOptions)),
                OutputFormat.Excel => Convert.FromBase64String(result["excel_base64"]?.GetValue<string>()
                    ?? throw new TextInException("Response has no excel_base64.")),
                _ => Encoding.UTF8.GetBytes(GetMarkdown(result)),
            };

            if (output == "-")
            {
                using Stream stdout = Console.OpenStandardOutput();
                stdout.Write(bytes);
                log.Line($"Wrote {Reporter.FormatSize(bytes.Length)} to stdout.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllBytes(output, bytes);
                log.Line($"Saved {output} ({Reporter.FormatSize(bytes.Length)}).");
            }
        }

        /// <summary>
        /// lite mode returns no markdown, only `elements`; join their text the same way as TextIn's
        /// convert_elements_to_legacy_format: newline-separated, glued when the previous one continues.
        /// </summary>
        static string GetMarkdown(JsonObject result)
        {
            if (result["markdown"] is JsonValue markdown)
                return markdown.GetValue<string>();
            if (result["elements"] is not JsonArray elements)
                throw new TextInException("Response has no markdown.");

            var sb = new StringBuilder();
            bool prevContinues = true; // nothing goes before the first element
            foreach (JsonNode? element in elements)
            {
                if (!prevContinues)
                    sb.Append('\n');
                sb.Append(element?["text"]?.GetValue<string>());
                prevContinues = element?["metadata"]?["is_continue"]?.GetValue<bool>() == true;
            }
            return sb.ToString();
        }
    }
}
