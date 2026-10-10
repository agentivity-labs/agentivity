namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Solves the weighted set-cover problem for topic reduction.
/// Step 3 of the paper: reduce topics while maximizing semantic coverage.
///
/// Universe = pages
/// Sets = topics (each topic covers the pages it's associated with)
/// Weights = sum of ρ(page, topic) for all pages
///
/// Goal: select minimal set of topics that covers all pages with maximum weight.
/// </summary>
public sealed class TopicSetCoverSolver
{
    private readonly TopicSetCoverOptions _options;

    public TopicSetCoverSolver(TopicSetCoverOptions? options = null)
    {
        _options = options ?? new TopicSetCoverOptions();
    }

    /// <summary>
    /// Solve the weighted set-cover problem using a greedy approximation.
    /// Returns the selected topic IDs.
    /// </summary>
    public HashSet<string> SolveGreedy(TopicalGraph graph)
    {
        var allPageUrls = graph.Pages.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncoveredPages = new HashSet<string>(allPageUrls, StringComparer.OrdinalIgnoreCase);
        var selectedTopics = new HashSet<string>();

        // Filter out overly generic topics (cover too many pages)
        var candidateTopics = graph.Topics.Values.Where(t => !IsOverlyGeneric(t, allPageUrls.Count)).ToList();

        // Greedy selection: pick topic with best weight-to-coverage ratio
        while (uncoveredPages.Count > 0 && candidateTopics.Count > 0)
        {
            TopicNode? bestTopic = null;
            double bestScore = double.MinValue;

            foreach (TopicNode? topic in candidateTopics)
            {
                // Pages this topic would newly cover
                int newlyCovered = topic.PageWeights.Keys.Count(p => uncoveredPages.Contains(p));

                if (newlyCovered == 0)
                    continue;

                // Score = total weight * newly covered pages (greedy heuristic)
                // Prioritize topics with high ρ values that cover uncovered pages
                double uncoveredWeight = topic.PageWeights.Where(kv => uncoveredPages.Contains(kv.Key)).Sum(kv => kv.Value);

                double score = uncoveredWeight * Math.Log(1 + newlyCovered);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTopic = topic;
                }
            }

            if (bestTopic == null)
                break;

            // Select this topic
            _ = selectedTopics.Add(bestTopic.Id);
            bestTopic.IsSelected = true;
            _ = candidateTopics.Remove(bestTopic);

            // Mark pages as covered
            foreach (string pageUrl in bestTopic.PageWeights.Keys)
            {
                _ = uncoveredPages.Remove(pageUrl);
            }

            // Stop if we have enough topics
            if (selectedTopics.Count >= _options.MaxTopics)
                break;
        }

        // Mark unselected topics
        foreach (TopicNode topic in graph.Topics.Values)
        {
            topic.IsSelected = selectedTopics.Contains(topic.Id);
        }

        return selectedTopics;
    }

    /// <summary>
    /// Alternative: solve using weighted set-cover with diversity bonus.
    /// Prefers semantically diverse topics.
    /// </summary>
    public HashSet<string> SolveWithDiversity(TopicalGraph graph)
    {
        var allPageUrls = graph.Pages.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncoveredPages = new HashSet<string>(allPageUrls, StringComparer.OrdinalIgnoreCase);
        var selectedTopics = new HashSet<string>();

        // Filter overly generic topics
        var candidateTopics = graph.Topics.Values.Where(t => !IsOverlyGeneric(t, allPageUrls.Count)).ToList();

        while (uncoveredPages.Count > 0 && candidateTopics.Count > 0)
        {
            TopicNode? bestTopic = null;
            double bestScore = double.MinValue;

            foreach (TopicNode? topic in candidateTopics)
            {
                int newlyCovered = topic.PageWeights.Keys.Count(p => uncoveredPages.Contains(p));

                if (newlyCovered == 0)
                    continue;

                double uncoveredWeight = topic.PageWeights.Where(kv => uncoveredPages.Contains(kv.Key)).Sum(kv => kv.Value);

                // Diversity bonus: prefer topics dissimilar to already selected
                double diversityBonus = 1.0;
                if (selectedTopics.Count > 0 && topic.Embedding != null)
                {
                    float maxSimilarity = selectedTopics.Select(id => graph.Topics.TryGetValue(id, out TopicNode? t) ? t : null).Where(t => t?.Embedding != null).Select(t => CosineSimilarity(topic.Embedding, t!.Embedding!)).DefaultIfEmpty(0).Max();

                    diversityBonus = 1.0 - (maxSimilarity * _options.DiversityPenalty);
                }

                double score = uncoveredWeight * Math.Log(1 + newlyCovered) * diversityBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTopic = topic;
                }
            }

            if (bestTopic == null)
                break;

            _ = selectedTopics.Add(bestTopic.Id);
            bestTopic.IsSelected = true;
            _ = candidateTopics.Remove(bestTopic);

            foreach (string pageUrl in bestTopic.PageWeights.Keys)
            {
                _ = uncoveredPages.Remove(pageUrl);
            }

            if (selectedTopics.Count >= _options.MaxTopics)
                break;
        }

        foreach (TopicNode topic in graph.Topics.Values)
        {
            topic.IsSelected = selectedTopics.Contains(topic.Id);
        }

        return selectedTopics;
    }

    private bool IsOverlyGeneric(TopicNode topic, int totalPages)
    {
        // A topic is too generic if it covers more than X% of all pages
        double coverageRatio = (double)topic.PageCount / totalPages;
        return coverageRatio > _options.MaxCoverageRatio;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;

        float dotProduct = 0,
            normA = 0,
            normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom > 1e-10f ? dotProduct / denom : 0;
    }
}

/// <summary>
/// Options for set-cover solver
/// </summary>
public sealed class TopicSetCoverOptions
{
    /// <summary>Maximum number of topics to select</summary>
    public int MaxTopics { get; set; } = 100;

    /// <summary>Maximum coverage ratio for a topic (above = too generic)</summary>
    public double MaxCoverageRatio { get; set; } = 0.8;

    /// <summary>Penalty for selecting similar topics (0 = no penalty, 1 = full penalty)</summary>
    public double DiversityPenalty { get; set; } = 0.3;
}
