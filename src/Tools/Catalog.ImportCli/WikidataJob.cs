namespace Catalog.ImportCli;

/// <summary>
/// The jobs `tvtracker-import wikidata` runs, and where each lives: "atlas" and "fill" in the sync worker, "research"
/// in Research.API. The names are the services' own.
/// </summary>
public static class WikidataJob
{
    public const string Atlas = "atlas";
    public const string Fill = "fill";
    public const string Research = "research";

    public static readonly string[] Names = [Atlas, Fill, Research];

    public const string SyncJobsPath = "api/v1/wikidata/jobs";
    public const string ResearchJobsPath = "api/v1/research/checks";

    public static string RunPath(string name) => name == Research ? $"{ResearchJobsPath}/run" : $"{SyncJobsPath}/{name}/run";

    public static SyncWorkerClient ClientFor(string name, SyncWorkerClient sync, SyncWorkerClient research) => name == Research ? research : sync;
}
