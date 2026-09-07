using PromptQueue.Core.Models;
using PromptQueue.Core.Operator;
using PromptQueue.Core.Serialization;

namespace PromptQueue.Core.Storage;

public static class TaskCsvImporter
{
    public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors)
    {
        public string Summary => Errors.Count == 0
            ? $"Imported {Imported} task(s); skipped {Skipped} duplicate ID(s)."
            : $"Imported {Imported} task(s); skipped {Skipped} duplicate ID(s); {Errors.Count} row error(s).";
    }

    public static ImportResult Import(Project project, string csv)
    {
        if (project.HasLoadError)
            return new ImportResult(0, 0, new[] { project.LoadError });

        TaskCsvSerializer.ParseResult parsed;
        try { parsed = TaskCsvSerializer.Parse(csv); }
        catch (FormatException ex)
        {
            return new ImportResult(0, 0, new[] { ex.Message });
        }

        var imported = 0;
        var skipped = 0;
        using (new CrossProcessLock(OperatorEngine.MutexName))
        {
            ProjectStore.ReloadTasks(project);
            if (project.HasLoadError)
                return new ImportResult(0, 0, new[] { project.LoadError });

            var ids = project.Tasks.Select(task => task.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var task in parsed.Tasks)
            {
                if (!ids.Add(task.Id))
                {
                    skipped++;
                    continue;
                }
                task.Order = project.Tasks.Count;
                project.Tasks.Add(task);
                AdvanceNextIndex(project, task.Id);
                imported++;
            }
            if (imported > 0) ProjectStore.Save(project);
        }
        return new ImportResult(imported, skipped, parsed.Errors);
    }

    private static void AdvanceNextIndex(Project project, string id)
    {
        var prefix = project.IdPrefix + "-";
        if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(id[prefix.Length..], out var number) && number >= project.NextIndex)
            project.NextIndex = number + 1;
    }
}
