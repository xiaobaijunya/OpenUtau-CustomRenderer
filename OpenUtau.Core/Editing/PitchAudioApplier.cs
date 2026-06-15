using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenUtau.Core.Analysis;
using OpenUtau.Core.Format;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Core.Editing;

/// <summary>
/// Standalone utility for extracting pitch from audio and applying it to voice parts,
/// specific notes, or tracks. Extracted from the audio-to-notes transcription pipeline.
/// </summary>
public static class PitchAudioApplier
{
    /// <summary>
    /// Extract pitch from a wave part and apply it as PITD (pitch deviation) curve
    /// to the target voice part.
    /// </summary>
    /// <param name="project">The current project.</param>
    /// <param name="targetPart">The target voice part to apply pitch to.</param>
    /// <param name="sourcePart">The source wave part to extract pitch from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if pitch was successfully applied; false if no pitch detected or cancelled.</returns>
    public static async Task<bool> ApplyPitchFromWavePartAsync(
        UProject project,
        UVoicePart targetPart,
        UWavePart sourcePart,
        CancellationToken cancellationToken = default)
    {
        if (targetPart.notes.Count == 0)
        {
            Log.Information("PitchAudioApplier: target part has no notes, skipping.");
            return false;
        }

        double srcStartMs = project.timeAxis.TickPosToMsPos(sourcePart.position);
        double srcSkipMs = sourcePart.GetSkipMs(project);
        double targetStartMs = project.timeAxis.TickPosToMsPos(targetPart.position);
        double targetEndMs = project.timeAxis.TickPosToMsPos(targetPart.End);
        double targetDurMs = targetEndMs - targetStartMs;

        double startSrcFileMs = Math.Max(0, targetStartMs - srcStartMs + srcSkipMs - 1000);
        double endSrcFileMs = Math.Min(sourcePart.fileDurationMs, targetEndMs - srcStartMs + srcSkipMs + 1000);

        if (endSrcFileMs <= startSrcFileMs)
        {
            Log.Information("PitchAudioApplier: no overlapping pitch region between target and source.");
            return false;
        }
        endSrcFileMs = Math.Max(0, endSrcFileMs);

        RmvpeResult? srcResult;
        using (var rmvpe = new RmvpeTranscriber())
        {
            using (cancellationToken.Register(() => rmvpe.Interrupt()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                srcResult = await Task.Run(() => rmvpe.Infer(sourcePart, startSrcFileMs, endSrcFileMs), cancellationToken);
            }
        }

        if (srcResult == null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var frameMs = srcResult.TimeStepSeconds * 1000.0;
        int targetFrames = (int)Math.Ceiling(targetDurMs / frameMs) + 1;
        var targetMidi = new float[targetFrames];

        for (int i = 0; i < targetFrames; i++)
        {
            double currentTargetMs = i * frameMs;
            double absMs = targetStartMs + currentTargetMs;
            double srcFileMs = absMs - srcStartMs + srcSkipMs;

            int srcIdx = (int)Math.Round((srcFileMs - startSrcFileMs) / frameMs);
            if (srcIdx >= 0 && srcIdx < srcResult.MidiPitch.Length)
            {
                targetMidi[i] = srcResult.MidiPitch[srcIdx];
            }
            else
            {
                targetMidi[i] = float.NaN;
            }
        }

        if (targetMidi.All(float.IsNaN))
        {
            Log.Information("PitchAudioApplier: all pitch values are NaN, nothing to apply.");
            return false;
        }

        var targetResult = new RmvpeResult
        {
            TimeStepSeconds = srcResult.TimeStepSeconds,
            MidiPitch = targetMidi,
        };

        DocManager.Inst.StartUndoGroup("command.batch.note", true);
        targetResult.ApplyToPart(project, targetPart);
        DocManager.Inst.EndUndoGroup();

        Log.Information("PitchAudioApplier: successfully applied pitch from {SourceName} to target part.", sourcePart.DisplayName);
        return true;
    }

    /// <summary>
    /// Extract pitch from a wave part and apply it as PITD curve to specific notes
    /// within a voice part. Non-selected notes are not modified.
    /// </summary>
    /// <param name="project">The current project.</param>
    /// <param name="part">The voice part containing the notes.</param>
    /// <param name="selectedNotes">The specific notes to apply pitch to.</param>
    /// <param name="sourcePart">The source wave part to extract pitch from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if pitch was successfully applied; false otherwise.</returns>
    public static async Task<bool> ApplyPitchToNotesAsync(
        UProject project,
        UVoicePart part,
        List<UNote> selectedNotes,
        UWavePart sourcePart,
        CancellationToken cancellationToken = default)
    {
        if (selectedNotes.Count == 0)
        {
            Log.Information("PitchAudioApplier: no notes selected, skipping.");
            return false;
        }

        // Create a temporary UVoicePart containing only the selected notes
        // so we can use RmvpeResult.ApplyToPart with proper note segments.
        double srcStartMs = project.timeAxis.TickPosToMsPos(sourcePart.position);
        double srcSkipMs = sourcePart.GetSkipMs(project);

        int firstNotePos = selectedNotes.Min(n => n.position + part.position);
        int lastNoteEnd = selectedNotes.Max(n => n.End + part.position);

        double targetStartMs = project.timeAxis.TickPosToMsPos(firstNotePos);
        double targetEndMs = project.timeAxis.TickPosToMsPos(lastNoteEnd);
        double targetDurMs = targetEndMs - targetStartMs;

        double startSrcFileMs = Math.Max(0, targetStartMs - srcStartMs + srcSkipMs - 1000);
        double endSrcFileMs = Math.Min(sourcePart.fileDurationMs, targetEndMs - srcStartMs + srcSkipMs + 1000);

        if (endSrcFileMs <= startSrcFileMs)
        {
            Log.Information("PitchAudioApplier: no overlapping pitch region for selected notes.");
            return false;
        }
        endSrcFileMs = Math.Max(0, endSrcFileMs);

        RmvpeResult? srcResult;
        using (var rmvpe = new RmvpeTranscriber())
        {
            using (cancellationToken.Register(() => rmvpe.Interrupt()))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                srcResult = await Task.Run(() => rmvpe.Infer(sourcePart, startSrcFileMs, endSrcFileMs), cancellationToken);
            }
        }

        if (srcResult == null || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var frameMs = srcResult.TimeStepSeconds * 1000.0;
        int targetFrames = (int)Math.Ceiling(targetDurMs / frameMs) + 1;
        var targetMidi = new float[targetFrames];

        for (int i = 0; i < targetFrames; i++)
        {
            double currentTargetMs = i * frameMs;
            double absMs = targetStartMs + currentTargetMs;
            double srcFileMs = absMs - srcStartMs + srcSkipMs;

            int srcIdx = (int)Math.Round((srcFileMs - startSrcFileMs) / frameMs);
            if (srcIdx >= 0 && srcIdx < srcResult.MidiPitch.Length)
            {
                targetMidi[i] = srcResult.MidiPitch[srcIdx];
            }
            else
            {
                targetMidi[i] = float.NaN;
            }
        }

        if (targetMidi.All(float.IsNaN))
        {
            Log.Information("PitchAudioApplier: all pitch values are NaN for selected notes.");
            return false;
        }

        var targetResult = new RmvpeResult
        {
            TimeStepSeconds = srcResult.TimeStepSeconds,
            MidiPitch = targetMidi,
        };

        // Build custom note segments only for the selected notes,
        // but aligned to the first selected note's position.
        var noteSegments = selectedNotes
            .OrderBy(note => note.position)
            .Select(note =>
            {
                var onsetTick = part.position + note.position;
                var endTick = part.position + note.End;
                var onsetMs = project.timeAxis.TickPosToMsPos(onsetTick);
                var endMs = project.timeAxis.TickPosToMsPos(endTick);
                return new NoteSegmentData
                {
                    OnsetMs = onsetMs,
                    DurationMs = endMs - onsetMs,
                    Midi = note.tone,
                    Rest = false,
                };
            })
            .ToList();

        // Build a temporary part-like structure using NoteSegmentData
        // and apply the PITD curve manually
        if (!project.expressions.TryGetValue(Format.Ustx.PITD, out var descriptor))
        {
            Log.Information("PitchAudioApplier: PITD expression not found.");
            return false;
        }

        var curve = BuildPitdCurve(targetResult, project, part, noteSegments, descriptor);
        if (curve.xs.Count == 0)
        {
            Log.Information("PitchAudioApplier: no PITD curve points generated for selected notes.");
            return false;
        }

        DocManager.Inst.StartUndoGroup("command.batch.note", true);
        var oldCurve = part.curves.FirstOrDefault(c => c.abbr == Format.Ustx.PITD);
        var oldXs = oldCurve?.xs.ToArray();
        var oldYs = oldCurve?.ys.ToArray();
        DocManager.Inst.ExecuteCmd(new MergedSetCurveCommand(
            project,
            part,
            Format.Ustx.PITD,
            oldXs,
            oldYs,
            curve.xs.ToArray(),
            curve.ys.ToArray()));
        DocManager.Inst.EndUndoGroup();

        Log.Information("PitchAudioApplier: successfully applied pitch to {NoteCount} selected notes.", selectedNotes.Count);
        return true;
    }

    /// <summary>
    /// Extract pitch from a wave part and apply it as PITD curve to all voice parts
    /// in the specified track that overlap with the given time range.
    /// </summary>
    /// <param name="project">The current project.</param>
    /// <param name="track">The target track containing voice parts.</param>
    /// <param name="startTick">Start tick for the time range (project absolute).</param>
    /// <param name="endTick">End tick for the time range (project absolute).</param>
    /// <param name="sourcePart">The source wave part to extract pitch from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of parts that were successfully processed.</returns>
    public static async Task<int> ApplyPitchToTrackAsync(
        UProject project,
        UTrack track,
        int startTick,
        int endTick,
        UWavePart sourcePart,
        CancellationToken cancellationToken = default)
    {
        var targetParts = project.parts
            .OfType<UVoicePart>()
            .Where(p => p.trackNo == track.TrackNo)
            .Where(p => p.position < endTick && p.End > startTick)
            .OrderBy(p => p.position)
            .ToList();

        if (targetParts.Count == 0)
        {
            Log.Information("PitchAudioApplier: no voice parts found in track at the specified range.");
            return 0;
        }

        int successCount = 0;
        foreach (var part in targetParts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                bool success = await ApplyPitchFromWavePartAsync(project, part, sourcePart, cancellationToken);
                if (success)
                {
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PitchAudioApplier: failed to apply pitch to part {PartName}", part.DisplayName);
            }
        }

        return successCount;
    }

    /// <summary>
    /// Extract pitch from an audio file and apply it as PITD curve
    /// to the target voice part.
    /// </summary>
    /// <param name="project">The current project.</param>
    /// <param name="targetPart">The target voice part.</param>
    /// <param name="audioFilePath">Absolute path to the audio file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful; false otherwise.</returns>
    public static async Task<bool> ApplyPitchFromAudioFileAsync(
        UProject project,
        UVoicePart targetPart,
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        // Create a temporary UWavePart from the audio file
        var wavePart = new UWavePart
        {
            FilePath = audioFilePath,
            trackNo = targetPart.trackNo,
            position = targetPart.position,
        };
        wavePart.Load(project);

        return await ApplyPitchFromWavePartAsync(project, targetPart, wavePart, cancellationToken);
    }

    /// <summary>
    /// Check if RMVPE is available (model file exists).
    /// </summary>
    public static bool IsRmvpeInstalled() => RmvpeTranscriber.IsInstalled();

    /// <summary>
    /// Get the RMVPE model path.
    /// </summary>
    public static string GetRmvpeModelPath() => RmvpeTranscriber.GetModelPath();

    /// <summary>
    /// Internal data class for note segment information used during pitch application.
    /// </summary>
    struct NoteSegmentData
    {
        public double OnsetMs;
        public double DurationMs;
        public int Midi;
        public bool Rest;
    }

    /// <summary>
    /// Build a PITD curve from the extracted RMVPE result aligned to the given note segments.
    /// Replicates the logic from RmvpeResult.ApplyToPart's internal processing.
    /// </summary>
    static UCurve BuildPitdCurve(
        RmvpeResult result,
        UProject project,
        UVoicePart part,
        List<NoteSegmentData> notes,
        UExpressionDescriptor descriptor)
    {
        const double MaxEdgeTrimMs = 25.0;
        const double MaxEdgeTrimRatio = 0.15;
        const int MedianWindowRadius = 2;
        const double AdaptiveSpikeThresholdCents = 75.0;
        const double AdaptiveSpikeBlend = 0.7;
        const int MaxFilledGapSteps = 12;

        var curve = new UCurve(descriptor);
        var frameMs = result.TimeStepSeconds * 1000.0;
        var partStartMs = project.timeAxis.TickPosToMsPos(part.position);
        var pendingPoints = new List<(int x, int y)>();
        var pendingNoteIndex = -1;
        int noteIndex = 0;

        for (int i = 0; i < result.MidiPitch.Length; ++i)
        {
            var midiPitch = result.MidiPitch[i];
            var localTimeMs = i * frameMs;
            var absoluteTimeMs = partStartMs + localTimeMs;

            while (noteIndex + 1 < notes.Count && notes[noteIndex].OnsetMs + notes[noteIndex].DurationMs <= absoluteTimeMs)
            {
                noteIndex++;
            }
            if (noteIndex >= notes.Count)
            {
                break;
            }

            var note = notes[noteIndex];
            if (pendingPoints.Count > 0 && pendingNoteIndex != noteIndex)
            {
                AppendSmoothedPoints(curve, pendingPoints, MedianWindowRadius, AdaptiveSpikeThresholdCents, AdaptiveSpikeBlend, MaxFilledGapSteps);
                pendingPoints.Clear();
                pendingNoteIndex = -1;
            }

            bool isInNote = note.OnsetMs <= absoluteTimeMs && absoluteTimeMs < note.OnsetMs + note.DurationMs;
            if (!isInNote || note.Rest || float.IsNaN(midiPitch))
            {
                continue;
            }

            var noteOffsetMs = absoluteTimeMs - note.OnsetMs;
            var edgeTrimMs = Math.Min(MaxEdgeTrimMs, note.DurationMs * MaxEdgeTrimRatio);
            if (note.DurationMs > edgeTrimMs * 2 &&
                (noteOffsetMs < edgeTrimMs || note.DurationMs - noteOffsetMs <= edgeTrimMs))
            {
                continue;
            }

            var tick = project.timeAxis.MsPosToTickPos(absoluteTimeMs);
            var x = tick - part.position;
            var y = (int)Math.Round(Math.Clamp((midiPitch - note.Midi) * 100.0, descriptor.min, descriptor.max));
            var snappedX = (int)Math.Round((double)x / UCurve.interval) * UCurve.interval;

            pendingNoteIndex = noteIndex;
            if (pendingPoints.Count > 0 && pendingPoints[^1].x == snappedX)
            {
                pendingPoints[^1] = (snappedX, y);
            }
            else
            {
                pendingPoints.Add((snappedX, y));
            }
        }

        AppendSmoothedPoints(curve, pendingPoints, MedianWindowRadius, AdaptiveSpikeThresholdCents, AdaptiveSpikeBlend, MaxFilledGapSteps);
        curve.Simplify();
        return curve;
    }

    static void AppendSmoothedPoints(
        UCurve curve,
        List<(int x, int y)> points,
        int medianWindowRadius,
        double adaptiveSpikeThresholdCents,
        double adaptiveSpikeBlend,
        int maxFilledGapSteps)
    {
        if (points.Count == 0)
        {
            return;
        }

        var processedPoints = FillShortGaps(points, maxFilledGapSteps);
        var ys = processedPoints.Select(p => p.y).ToList();
        var smoothedYs = AdaptiveSmooth(MedianFilter(ys, medianWindowRadius), adaptiveSpikeThresholdCents, adaptiveSpikeBlend);

        for (int i = 0; i < processedPoints.Count; ++i)
        {
            var point = processedPoints[i];
            if (curve.xs.Count > 0 && curve.xs[^1] == point.x)
            {
                curve.ys[^1] = smoothedYs[i];
            }
            else
            {
                curve.xs.Add(point.x);
                curve.ys.Add(smoothedYs[i]);
            }
        }
    }

    static List<int> MedianFilter(IReadOnlyList<int> values, int windowRadius)
    {
        var result = new List<int>(values.Count);
        for (int i = 0; i < values.Count; ++i)
        {
            var window = new List<int>();
            for (int j = Math.Max(0, i - windowRadius); j <= Math.Min(values.Count - 1, i + windowRadius); ++j)
            {
                window.Add(values[j]);
            }
            window.Sort();
            result.Add(window[window.Count / 2]);
        }
        return result;
    }

    static List<int> AdaptiveSmooth(IReadOnlyList<int> values, double spikeThresholdCents, double spikeBlend)
    {
        if (values.Count <= 2)
        {
            return values.ToList();
        }
        var result = values.ToList();
        for (int i = 1; i < values.Count - 1; ++i)
        {
            var neighborAverage = (result[i - 1] + result[i + 1]) / 2.0;
            var delta = result[i] - neighborAverage;
            if (Math.Abs(delta) <= spikeThresholdCents)
            {
                continue;
            }
            result[i] = (int)Math.Round(result[i] - delta * spikeBlend);
        }
        return result;
    }

    static List<(int x, int y)> FillShortGaps(List<(int x, int y)> points, int maxFilledGapSteps)
    {
        if (points.Count == 0)
        {
            return points;
        }
        var expanded = new List<(int x, int y)>();
        expanded.Add(points[0]);
        for (int i = 1; i < points.Count; ++i)
        {
            var prev = expanded[^1];
            var current = points[i];
            var gapSteps = Math.Max(0, (current.x - prev.x) / UCurve.interval - 1);
            if (gapSteps > 0 && gapSteps <= maxFilledGapSteps)
            {
                for (int step = 1; step <= gapSteps; ++step)
                {
                    var ratio = step / (double)(gapSteps + 1);
                    expanded.Add((
                        prev.x + step * UCurve.interval,
                        (int)Math.Round(prev.y + (current.y - prev.y) * ratio)
                    ));
                }
            }
            expanded.Add(current);
        }
        return expanded;
    }
}
