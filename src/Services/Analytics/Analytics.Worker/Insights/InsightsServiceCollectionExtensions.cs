namespace Analytics.Worker.Insights;

public static class InsightsServiceCollectionExtensions
{
    /// <summary>
    /// The insights pipeline and every insight. Adding an insight is one line here plus its class: a stage for a part
    /// of the show document, a rule for a tag, a list for a table built across shows, or a publication for a part other
    /// services keep a copy of.
    /// </summary>
    public static IServiceCollection AddInsights(this IServiceCollection services)
    {
        services.AddScoped<InsightsPipeline>();

        // Show document stages, run in this order (a stage sees what earlier ones set).
        services.AddSingleton<IShowInsight, SeasonsInsight>();
        services.AddSingleton<IShowInsight, TopEpisodesInsight>();
        services.AddSingleton<IShowInsight, RunInsight>();
        services.AddSingleton<IShowInsight, NextPremiereInsight>();
        services.AddSingleton<IShowInsight, SharedCastInsight>();
        services.AddSingleton<IShowInsight, PatternsInsight>();
        services.AddSingleton<IShowInsight, TagsInsight>();
        services.AddSingleton<IShowInsight, HistogramsInsight>();
        services.AddSingleton<IShowInsight, ViewingInsight>();
        services.AddSingleton<IShowInsight, MomentumInsight>();
        services.AddSingleton<IShowInsight, DnaInsight>();
        services.AddSingleton<IShowInsight, OutlookInsight>();
        services.AddSingleton<IShowInsight, GetsGoodInsight>();
        services.AddSingleton<IShowInsight, LandingInsight>();

        // Tag rules, in the order tags are shown.
        services.AddSingleton<IShowTagRule, DropsAtOnceRule>();
        services.AddSingleton<IShowTagRule, BingeableRule>();
        services.AddSingleton<IShowTagRule, WeekendBingeRule>();
        services.AddSingleton<IShowTagRule, FanFavoriteRule>();
        services.AddSingleton<IShowTagRule, LimitedSeriesRule>();
        services.AddSingleton<IShowTagRule, QuickEpisodesRule>();
        services.AddSingleton<IShowTagRule, LongRunningRule>();
        services.AddSingleton<IShowTagRule, ProlificMakerRule>();

        // Parts of the document other services keep a copy of, published when they change.
        services.AddSingleton<IInsightPublication, TagsPublication>();
        services.AddSingleton<IInsightPublication, DnaPublication>();

        // Lists built across shows; each sees every show's finished document.
        services.AddSingleton<IListInsight, PremieresList>();
        services.AddSingleton<IListInsight, TopEpisodesList>();
        services.AddSingleton<IListInsight, DiscoveryList>();
        services.AddSingleton<IListInsight, PersonInsightsList>();
        services.AddSingleton<IListInsight, CreatorFingerprintList>();

        return services;
    }
}
