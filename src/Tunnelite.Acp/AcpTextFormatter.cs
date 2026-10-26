using System.Text;
using Acplite.Sdk.Formatting;
using Acplite.Sdk.Protocol;

namespace Tunnelite.Acp;

internal static class AcpTextFormatter
{
    public const string ActivityLogSeparator = " · ";
    private const int ShortSessionIdLength = 8;
    private const int MaxSessionTitleLength = 72;
    private const int TruncatedSessionTitlePrefixLength = 69;

    public static string BuildSessionKey(AgentProviderKind provider, string sessionId)
        => $"{provider.ToShortId()}:{sessionId}";

    public static string ShortId(string id)
        => id.Length <= ShortSessionIdLength ? id : id[..ShortSessionIdLength];

    public static string CreateDefaultSessionTitle(string sessionId)
        => $"Session {ShortId(sessionId)}";

    public static string CreateNewSessionTitle(AgentProviderKind provider)
        => $"New {provider.ToDisplayName()} Session";

    public static bool IsPlaceholderSessionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        return title.StartsWith("Session ", StringComparison.Ordinal) ||
               title.StartsWith("New ", StringComparison.Ordinal) ||
               title.StartsWith("Codex session ", StringComparison.OrdinalIgnoreCase);
    }

    public static string TruncateTitle(string text)
    {
        var firstLine = text.Split('\n', '\r')[0].Trim();
        return firstLine.Length <= MaxSessionTitleLength
            ? firstLine
            : firstLine[..TruncatedSessionTitlePrefixLength] + "...";
    }

    public static string? CleanUserTranscriptText(string? text)
        => string.IsNullOrWhiteSpace(text) ? text : TextSanitizer.ExtractAntigravityUserPrompt(text);

    public static string? NormalizeToolText(string? toolName, string? text)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmed) &&
            !string.Equals(trimmed, toolName, StringComparison.OrdinalIgnoreCase))
        {
            return TruncateLargePayload(trimmed, AcpGatewayDefaults.MaxToolSummaryChars);
        }

        return null;
    }

    public static string? FormatThoughtPreview(string? rawThinking, int maxLen = AcpGatewayDefaults.DefaultThoughtPreviewChars)
    {
        if (string.IsNullOrWhiteSpace(rawThinking))
        {
            return null;
        }

        var sb = new StringBuilder(Math.Min(rawThinking.Length, maxLen + 32));
        var prevSpace = true;
        foreach (var ch in rawThinking)
        {
            if (ch is '*' or '`' or '_' or '#')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
                continue;
            }

            sb.Append(ch);
            prevSpace = false;
            if (sb.Length >= maxLen + 16)
            {
                break;
            }
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length <= maxLen)
        {
            return cleaned;
        }

        var cut = cleaned.LastIndexOf(' ', maxLen);
        if (cut < maxLen / 2)
        {
            cut = maxLen;
        }

        return cleaned[..cut].TrimEnd() + "…";
    }

    public static string? TruncateLargePayload(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "\n... [truncated]";
    }

    public static string FormatActivityLogEntry(string toolName, string? summaryText)
    {
        return !string.IsNullOrWhiteSpace(summaryText)
            ? $"{toolName}{ActivityLogSeparator}{summaryText}"
            : toolName;
    }

    public static (string ToolName, string? TargetSummary) ParseActivityLogEntry(string logLine)
    {
        var separatorIndex = logLine.IndexOf(ActivityLogSeparator, StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            return (logLine, null);
        }

        var toolName = logLine[..separatorIndex];
        var targetSummary = logLine[(separatorIndex + ActivityLogSeparator.Length)..];
        return (toolName, targetSummary);
    }
}
