using Spectre.Console;
using Vespa.Feed;
using Vespa.Models;

namespace Vespa.Samples;

public static class OutputFormatter
{
    public static void PrintHeader()
    {
        var panel = new Panel(
            Align.Center(new Markup(
                "[bold cyan]Vespa.NET Feature Demo[/]\n\n" +
                "[dim]All 8 API surfaces in one run[/]")))
        {
            Border = BoxBorder.Double,
            Padding = new Padding(2, 1)
        };
        panel.BorderColor(Color.Yellow);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static void PrintHealthStatus(bool isHealthy, string endpoint, double responseTimeMs = 0)
    {
        var statusIcon = isHealthy ? "[green]✓[/]" : "[red]✗[/]";
        var statusText = isHealthy ? "[green]HEALTHY[/]" : "[red]UNHEALTHY[/]";

        var table = new Table();
        table.AddColumn(new TableColumn("").LeftAligned());
        table.AddColumn(new TableColumn("").LeftAligned());
        table.Border(TableBorder.Rounded);
        table.HideHeaders();
        table.AddRow($"{statusIcon} Vespa", statusText);
        table.AddRow("📍 Endpoint", endpoint);
        if (isHealthy && responseTimeMs > 0)
            table.AddRow("⚡ Latency", $"{responseTimeMs:F0}ms");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintSectionHeader(string title, string? emoji = null)
    {
        var text = emoji != null ? $"{emoji}  {title}" : title;
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold yellow]{text}[/]").LeftJustified());
        AnsiConsole.WriteLine();
    }

    public static void PrintBulkOperationStart(int totalDocuments)
        => AnsiConsole.MarkupLine($"[bold]Indexing {totalDocuments:N0} documents...[/]");

    public static void PrintBulkOperationResult(FeedResult result)
    {
        var successRate = result.SuccessRate * 100;
        var throughput = result.TotalDocuments / Math.Max(result.Duration.TotalSeconds, 0.001);
        var color = result.IsSuccess ? "green" : (successRate > 50 ? "yellow" : "red");
        var icon = result.IsSuccess ? "✓" : "⚠";

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(
            new Markup($"[{color}]{icon}[/] Completed:"),
            new Markup($"[bold]{result.SuccessCount:N0}[/] / {result.TotalDocuments:N0} ([{color}]{successRate:F1}%[/])"));
        grid.AddRow(new Markup("⏱  Duration:"), new Markup($"[bold]{result.Duration.TotalSeconds:F2}s[/]"));
        grid.AddRow(new Markup("🚀 Throughput:"), new Markup($"[bold]{throughput:F0}[/] docs/sec"));

        var panel = new Panel(grid) { Border = BoxBorder.Rounded, Padding = new Padding(1, 0) };
        panel.BorderColor(result.IsSuccess ? Color.Green : Color.Yellow);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public static void PrintNeighborsTable(IReadOnlyList<SearchHit<Transaction>> neighbors)
    {
        var table = new Table();
        table.Title($"[bold]Top {neighbors.Count} Nearest Neighbors[/]");
        table.AddColumn(new TableColumn("Rank").Centered());
        table.AddColumn(new TableColumn("ID").LeftAligned());
        table.AddColumn(new TableColumn("Amount").RightAligned());
        table.AddColumn(new TableColumn("Category").LeftAligned());
        table.AddColumn(new TableColumn("Closeness").RightAligned());
        table.Border(TableBorder.Rounded);

        for (var i = 0; i < neighbors.Count; i++)
        {
            var n = neighbors[i];
            var color = n.Relevance switch
            {
                > 0.01 => "green",
                > 0.005 => "yellow",
                > 0.001 => "orange1",
                _ => "red"
            };

            table.AddRow(
                $"[dim]{i + 1}[/]",
                $"[dim]{n.Id}[/]",
                $"{n.Fields!.Amount:N2}",
                n.Fields.Category,
                $"[{color}]{n.Relevance:F6}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public static void PrintGroupingResults(IReadOnlyList<VespaGroupList> groupingResults)
    {
        if (groupingResults.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No grouping results returned[/]");
            return;
        }

        foreach (var groupList in groupingResults)
        {
            var table = new Table();
            table.Title($"[bold]Group by: {groupList.Label}[/]");
            table.AddColumn(new TableColumn("Value").LeftAligned());
            table.AddColumn(new TableColumn("Count").RightAligned());
            table.Border(TableBorder.Rounded);

            foreach (var group in groupList.Groups)
            {
                var count = group.Aggregations.TryGetValue("count()", out var c) ? c.ToString("N0") : "-";
                table.AddRow($"[cyan]{group.Value}[/]", $"[bold]{count}[/]");
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.WriteLine();
    }
}
