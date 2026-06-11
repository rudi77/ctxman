using System.Text.Json;
using Ctxman.Core.Domain;
using Ctxman.Core.Storage;

namespace Ctxman.Core.Rendering;

/// <summary>
/// Diff der Static-Region über <c>source</c> und Tool-Namen der <c>tool_def</c>-Segmente (Spec §4.2).
/// </summary>
public sealed record StaticRegionDiffResult(
    IReadOnlyList<string> AddedTools,
    IReadOnlyList<string> RemovedTools,
    IReadOnlyList<string> AddedSources,
    IReadOnlyList<string> RemovedSources);

/// <summary>
/// Berechnet Tool-/Source-Diffs und wendet <c>on_tool_removed</c> auf betroffene Working-Units an (Spec §4.2).
/// </summary>
public static class StaticRegionDiff
{
    public static StaticRegionDiffResult Compute(
        IReadOnlyList<Segment> oldStatic,
        IReadOnlyList<StaticSegmentInput> newSegments)
    {
        var oldTools = ExtractToolNames(oldStatic);
        var newTools = ExtractToolNames(newSegments);
        var oldSources = ExtractSources(oldStatic);
        var newSources = ExtractSources(newSegments);

        return new StaticRegionDiffResult(
            AddedTools: newTools.Except(oldTools, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            RemovedTools: oldTools.Except(newTools, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            AddedSources: newSources.Except(oldSources, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            RemovedSources: oldSources.Except(newSources, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList());
    }

    public static async Task ApplyOnToolRemovedAsync(
        Session session,
        IReadOnlyList<Segment> workingSegments,
        StaticRegionDiffResult diff,
        IBlobStore blobStore,
        string tenantId,
        CancellationToken ct)
    {
        if (diff.RemovedTools.Count == 0 && diff.RemovedSources.Count == 0)
        {
            return;
        }

        var removedToolSet = diff.RemovedTools.ToHashSet(StringComparer.Ordinal);
        var removedSourceSet = diff.RemovedSources.ToHashSet(StringComparer.Ordinal);

        var openToolCallIds = workingSegments
            .Where(s => s.Kind == "tool_call" && s.ToolCallId is not null)
            .Where(s => ReferencesRemovedTool(s, removedToolSet, removedSourceSet))
            .Select(s => s.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        if (openToolCallIds.Count == 0)
        {
            return;
        }

        switch (session.Policy.OnToolRemoved)
        {
            case "keep":
                return;

            case "evict":
                foreach (var segment in workingSegments)
                {
                    if (segment.State is not (SegmentState.Live or SegmentState.Externalized))
                    {
                        continue;
                    }

                    if (segment.Kind is "tool_call" or "tool_result"
                        && segment.ToolCallId is not null
                        && openToolCallIds.Contains(segment.ToolCallId))
                    {
                        segment.Evict();
                    }
                }

                return;

            default: // externalize (Spec §4.2 Default)
                foreach (var segment in workingSegments)
                {
                    if (segment.Kind != "tool_result"
                        || segment.ToolCallId is null
                        || !openToolCallIds.Contains(segment.ToolCallId)
                        || segment.State != SegmentState.Live
                        || segment.Content is null)
                    {
                        continue;
                    }

                    await ExternalizeSegmentAsync(segment, blobStore, tenantId, ct);
                }

                return;
        }
    }

    private static bool ReferencesRemovedTool(
        Segment toolCall,
        HashSet<string> removedTools,
        HashSet<string> removedSources)
    {
        if (toolCall.Source is { } source)
        {
            if (removedTools.Contains(source) || removedSources.Contains(source))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ExternalizeSegmentAsync(
        Segment segment,
        IBlobStore blobStore,
        string tenantId,
        CancellationToken ct)
    {
        var content = segment.Content ?? string.Empty;
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        var blobRef = await blobStore.Put(tenantId, stream, "text/plain", ct);
        var summary = content.Length <= 200 ? content : content[..200] + "…";
        segment.Externalize(blobRef, summary);
    }

    private static HashSet<string> ExtractToolNames(IReadOnlyList<Segment> segments) =>
        segments
            .Where(s => s.Kind == "tool_def")
            .Select(s => TryExtractToolName(s.Content))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ExtractToolNames(IReadOnlyList<StaticSegmentInput> segments) =>
        segments
            .Where(s => s.Kind == "tool_def")
            .Select(s => TryExtractToolName(s.Content))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ExtractSources(IReadOnlyList<Segment> segments) =>
        segments
            .Select(s => s.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ExtractSources(IReadOnlyList<StaticSegmentInput> segments) =>
        segments
            .Select(s => s.Source)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToHashSet(StringComparer.Ordinal);

    private static string? TryExtractToolName(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }
        }
        catch (JsonException)
        {
            // Nicht-JSON tool_def: gesamter Content als Name-Fallback.
            return content.Trim();
        }

        return null;
    }
}

/// <summary>Neutraler Static-Segment-Input für Diff und Epoch-Bump (Spec §4.2).</summary>
public sealed record StaticSegmentInput(
    string Kind,
    string? Role,
    string Content,
    string? Source);
