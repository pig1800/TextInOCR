namespace TextInOCR
{
    /// <summary>
    /// Progress messages: on stdout, or on stderr when the result itself is written to stdout.
    /// Status lines are rewritten in place with \r on a console; when redirected, only final ones are printed.
    /// </summary>
    internal sealed class Reporter(bool toStderr)
    {
        readonly TextWriter writer = toStderr ? Console.Error : Console.Out;
        int statusLength; // length of the status line currently shown, 0 if none

        public bool Interactive { get; } = !(toStderr ? Console.IsErrorRedirected : Console.IsOutputRedirected);

        public void Line(string message = "")
        {
            EndStatus();
            writer.WriteLine(message);
        }

        public void Status(string message, bool final = false)
        {
            if (Interactive)
            {
                writer.Write("\r" + message.PadRight(statusLength));
                statusLength = Math.Max(message.Length, 1);
                if (final) EndStatus();
            }
            else if (final)
                writer.WriteLine(message);
        }

        /// <summary>Moves past an in-place status line, so the next output starts on a fresh line.</summary>
        public void EndStatus()
        {
            if (statusLength == 0) return;
            writer.WriteLine();
            statusLength = 0;
        }

        public static string FormatSize(double bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024:F2} MB" : $"{bytes / 1024:F2} KB";

        public static string FormatDuration(TimeSpan time) =>
            time.TotalSeconds >= 60 ? $"{(int)time.TotalMinutes} mins {time.Seconds} secs" : $"{time.TotalSeconds:0.0} secs";
    }
}
