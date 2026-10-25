namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.Seo.PagesClustering.TopicalClustering;

/// <summary>
/// Spectral clustering algorithm for the topic graph.
/// Implements hierarchical bisection using normalized Laplacian and Fiedler vector.
/// Based on "Topical Clustering of Search Results" (Scaiella et al.)
/// </summary>
public sealed class SpectralClusterer
{
    private readonly SpectralClusteringOptions _options;

    public SpectralClusterer(SpectralClusteringOptions? options = null)
    {
        _options = options ?? new SpectralClusteringOptions();
    }

    /// <summary>
    /// Perform hierarchical spectral clustering on the topic graph.
    /// Returns topic cluster assignments.
    /// </summary>
    public List<TopicClusterResult> ClusterTopics(TopicGraphMatrix graph)
    {
        if (graph.Size == 0)
            return [];

        if (graph.Size == 1)
        {
            return [new() { ClusterId = 0, TopicIds = [graph.Topics[0].Id] }];
        }

        // Initial cluster contains all topics
        var initialCluster = new ClusterState { TopicIndices = Enumerable.Range(0, graph.Size).ToList(), ClusterId = 0 };

        var clusters = new List<ClusterState> { initialCluster };
        int nextClusterId = 1;
        int iterations = 0;

        // Hierarchical bisection loop
        while (iterations < _options.MaxIterations)
        {
            iterations++;

            // Find the cluster to split:
            // - Largest cluster with lowest λ₂ (weakest cohesion)
            (ClusterState Cluster, double Lambda2) candidatesForSplit = clusters
                .Where(c => c.TopicIndices.Count >= _options.MinClusterSize * 2)
                .Select(c => (Cluster: c, Lambda2: ComputeLambda2(graph, c.TopicIndices)))
                .Where(x => x.Lambda2 < _options.CohesionThreshold)
                .OrderBy(x => x.Lambda2) // Lowest cohesion first
                .ThenByDescending(x => x.Cluster.TopicIndices.Count) // Largest first
                .FirstOrDefault();

            if (candidatesForSplit.Cluster == null)
                break; // No more clusters to split

            ClusterState clusterToSplit = candidatesForSplit.Cluster;

            // Bisect using Fiedler vector (second eigenvector)
            (List<int>? left, List<int>? right) = BisectCluster(graph, clusterToSplit.TopicIndices);

            if (left.Count < _options.MinClusterSize || right.Count < _options.MinClusterSize)
                break; // Split would create too-small clusters

            // Replace original cluster with two new ones
            _ = clusters.Remove(clusterToSplit);

            clusters.Add(
                new ClusterState
                {
                    TopicIndices = left,
                    ClusterId = clusterToSplit.ClusterId, // Reuse ID for one
                }
            );

            clusters.Add(new ClusterState { TopicIndices = right, ClusterId = nextClusterId++ });
        }

        // Convert to result format
        return clusters.Select(c => new TopicClusterResult { ClusterId = c.ClusterId, TopicIds = c.TopicIndices.Select(i => graph.Topics[i].Id).ToList() }).ToList();
    }

    /// <summary>
    /// Compute λ₂ (second smallest eigenvalue of normalized Laplacian) for a subgraph.
    /// This indicates cluster cohesion - lower = less cohesive = should split.
    /// </summary>
    private double ComputeLambda2(TopicGraphMatrix fullGraph, List<int> indices)
    {
        int n = indices.Count;
        if (n <= 1)
            return double.MaxValue;

        // Extract subgraph adjacency matrix
        double[,] subAdj = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                subAdj[i, j] = fullGraph.AdjacencyMatrix[indices[i], indices[j]];
            }
        }

        // Compute normalized Laplacian: L_norm = I - D^(-1/2) * A * D^(-1/2)
        double[,] laplacian = ComputeNormalizedLaplacian(subAdj, n);

        // Compute eigenvalues using power iteration for second smallest
        double[] eigenvalues = ComputeSmallestEigenvalues(laplacian, n, 2);

        return eigenvalues.Length >= 2 ? eigenvalues[1] : double.MaxValue;
    }

    /// <summary>
    /// Bisect a cluster by computing the Fiedler vector and splitting by sign.
    /// Minimizes normalized cut.
    /// </summary>
    private (List<int> Left, List<int> Right) BisectCluster(TopicGraphMatrix fullGraph, List<int> indices)
    {
        int n = indices.Count;
        if (n <= 1)
            return (indices.ToList(), new List<int>());

        // Extract subgraph
        double[,] subAdj = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                subAdj[i, j] = fullGraph.AdjacencyMatrix[indices[i], indices[j]];
            }
        }

        // Compute normalized Laplacian
        double[,] laplacian = ComputeNormalizedLaplacian(subAdj, n);

        // Compute Fiedler vector (eigenvector for second smallest eigenvalue)
        double[] fiedlerVector = ComputeFiedlerVector(laplacian, n);

        // Split by sign of Fiedler vector (classic spectral bisection)
        var left = new List<int>();
        var right = new List<int>();

        for (int i = 0; i < n; i++)
        {
            if (fiedlerVector[i] < 0)
                left.Add(indices[i]);
            else
                right.Add(indices[i]);
        }

        // Ensure both sides have elements
        if (left.Count == 0)
        {
            left.Add(right[0]);
            right.RemoveAt(0);
        }
        else if (right.Count == 0)
        {
            right.Add(left[0]);
            left.RemoveAt(0);
        }

        return (left, right);
    }

    /// <summary>
    /// Compute normalized Laplacian matrix: L_norm = I - D^(-1/2) * A * D^(-1/2)
    /// </summary>
    private double[,] ComputeNormalizedLaplacian(double[,] adjacency, int n)
    {
        double[,] laplacian = new double[n, n];
        double[] degrees = new double[n];

        // Compute degrees
        for (int i = 0; i < n; i++)
        {
            double deg = 0;
            for (int j = 0; j < n; j++)
            {
                deg += adjacency[i, j];
            }
            degrees[i] = deg;
        }

        // Compute D^(-1/2)
        double[] dInvSqrt = new double[n];
        for (int i = 0; i < n; i++)
        {
            dInvSqrt[i] = degrees[i] > 1e-10 ? 1.0 / Math.Sqrt(degrees[i]) : 0.0;
        }

        // L_norm = I - D^(-1/2) * A * D^(-1/2)
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                laplacian[i, j] = i == j ? 1.0 - (dInvSqrt[i] * adjacency[i, j] * dInvSqrt[j]) : -dInvSqrt[i] * adjacency[i, j] * dInvSqrt[j];
            }
        }

        return laplacian;
    }

    /// <summary>
    /// Compute smallest eigenvalues using inverse power iteration.
    /// </summary>
    private double[] ComputeSmallestEigenvalues(double[,] matrix, int n, int count)
    {
        // Shift matrix to make smallest eigenvalue the largest
        // Then use power iteration

        // First, find approximate largest eigenvalue for shifting
        double maxEig = EstimateLargestEigenvalue(matrix, n);

        // Shifted matrix: B = maxEig*I - L
        double[,] shifted = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                shifted[i, j] = (i == j ? maxEig : 0) - matrix[i, j];
            }
        }

        var eigenvalues = new List<double>();
        var foundVectors = new List<double[]>();

        for (int k = 0; k < count; k++)
        {
            (double eigVal, double[]? eigVec) = PowerIterationDeflated(shifted, n, foundVectors);
            eigenvalues.Add(maxEig - eigVal); // Convert back to original eigenvalue
            foundVectors.Add(eigVec);
        }

        return eigenvalues.OrderBy(e => e).ToArray();
    }

    /// <summary>
    /// Compute Fiedler vector (eigenvector for second smallest eigenvalue)
    /// </summary>
    private double[] ComputeFiedlerVector(double[,] laplacian, int n)
    {
        // Same shift technique, but return the eigenvector
        double maxEig = EstimateLargestEigenvalue(laplacian, n);

        double[,] shifted = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                shifted[i, j] = (i == j ? maxEig : 0) - laplacian[i, j];
            }
        }

        // First eigenvector (corresponds to smallest eigenvalue = 0)
        (double _, double[]? firstVec) = PowerIterationDeflated(shifted, n, []);

        // Second eigenvector (Fiedler vector)
        (double _, double[]? fiedlerVec) = PowerIterationDeflated(shifted, n, [firstVec]);

        return fiedlerVec;
    }

    private double EstimateLargestEigenvalue(double[,] matrix, int n)
    {
        // Simple power iteration for largest eigenvalue estimate
        double[] v = new double[n];
        var random = new Random(42);
        for (int i = 0; i < n; i++)
            v[i] = random.NextDouble();
        Normalize(v);

        for (int iter = 0; iter < 20; iter++)
        {
            double[] newV = MatrixVectorMultiply(matrix, v, n);
            Normalize(newV);
            v = newV;
        }

        // Rayleigh quotient
        double[] Av = MatrixVectorMultiply(matrix, v, n);
        return DotProduct(v, Av);
    }

    private (double Eigenvalue, double[] Eigenvector) PowerIterationDeflated(double[,] matrix, int n, List<double[]> deflatedVectors)
    {
        double[] v = new double[n];
        var random = new Random(42);
        for (int i = 0; i < n; i++)
            v[i] = random.NextDouble();

        // Orthogonalize against previously found vectors
        foreach (double[] prev in deflatedVectors)
        {
            double proj = DotProduct(v, prev);
            for (int i = 0; i < n; i++)
                v[i] -= proj * prev[i];
        }
        Normalize(v);

        for (int iter = 0; iter < _options.PowerIterations; iter++)
        {
            double[] newV = MatrixVectorMultiply(matrix, v, n);

            // Orthogonalize against previously found vectors
            foreach (double[] prev in deflatedVectors)
            {
                double proj = DotProduct(newV, prev);
                for (int i = 0; i < n; i++)
                    newV[i] -= proj * prev[i];
            }

            Normalize(newV);
            v = newV;
        }

        // Compute eigenvalue via Rayleigh quotient
        double[] Av = MatrixVectorMultiply(matrix, v, n);
        double eigenvalue = DotProduct(v, Av);

        return (eigenvalue, v);
    }

    private double[] MatrixVectorMultiply(double[,] matrix, double[] vector, int n)
    {
        double[] result = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
            {
                sum += matrix[i, j] * vector[j];
            }
            result[i] = sum;
        }
        return result;
    }

    private void Normalize(double[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => x * x));
        if (norm > 1e-10)
        {
            for (int i = 0; i < v.Length; i++)
                v[i] /= norm;
        }
    }

    private double DotProduct(double[] a, double[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private class ClusterState
    {
        public List<int> TopicIndices { get; set; } = [];
        public int ClusterId { get; set; }
    }
}

/// <summary>
/// Options for spectral clustering
/// </summary>
public sealed class SpectralClusteringOptions
{
    /// <summary>Maximum iterations of hierarchical bisection</summary>
    public int MaxIterations { get; set; } = 50;

    /// <summary>Minimum cluster size (won't split below this)</summary>
    public int MinClusterSize { get; set; } = 2;

    /// <summary>λ₂ threshold for cluster cohesion (below = can split)</summary>
    public double CohesionThreshold { get; set; } = 0.5;

    /// <summary>Power iterations for eigenvalue computation</summary>
    public int PowerIterations { get; set; } = 100;
}

/// <summary>
/// Result of topic clustering
/// </summary>
public sealed class TopicClusterResult
{
    public int ClusterId { get; set; }
    public List<string> TopicIds { get; set; } = [];
}
