using Agentivity.Infrastructure.Workflows;
using Agentivity.Infrastructure.Workflows.Resources;
using Agentivity.Infrastructure.Workflows.Resources.Capabilities;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Agentivity.Infrastructure.Workflow.Catalog.Nodes.AI.Models.Capabilities;

/// <summary>
/// Local ONNX-based embedding capability.
/// Uses a local ONNX model (all-MiniLM-L6-v2) for creating embeddings without external API calls.
/// Perfect for Simple Memory - 100% offline, no credentials needed.
/// </summary>
public sealed class OnnxLocalEmbeddingCapability : IEmbeddingCapability, IDisposable
{
    private readonly InferenceSession _session;
    private readonly ILogger? _logger;
    private readonly SimpleTokenizer _tokenizer;

    public int EmbeddingDimensions => 384; // all-MiniLM-L6-v2 produces 384-dimensional embeddings
    public string ModelName => "all-MiniLM-L6-v2 (ONNX Local)";

    public OnnxLocalEmbeddingCapability(string modelPath, ILogger? logger = null)
    {
        _logger = logger;

        // Load vocabulary from vocab.txt (in same directory as model.onnx)
        string vocabPath = Path.Combine(Path.GetDirectoryName(modelPath)!, "vocab.txt");
        _tokenizer = new SimpleTokenizer(vocabPath, logger);

        try
        {
            var sessionOptions = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING };

            _session = new InferenceSession(modelPath, sessionOptions);
            _logger?.LogInformation("Loaded ONNX embedding model from {ModelPath}", modelPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load ONNX model from {ModelPath}", modelPath);
            throw new InvalidOperationException($"Failed to load ONNX embedding model: {ex.Message}", ex);
        }
    }

    public async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        return await Task.Run(
            () =>
            {
                // 1. Tokenize text
                long[] tokens = _tokenizer.Tokenize(text);

                // 2. Create input tensors (BERT needs 3 inputs: input_ids, attention_mask, token_type_ids)
                var inputIds = new DenseTensor<long>(new[] { 1, tokens.Length });
                var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Length });
                var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokens.Length });

                for (int i = 0; i < tokens.Length; i++)
                {
                    inputIds[0, i] = tokens[i];
                    attentionMask[0, i] = 1;
                    tokenTypeIds[0, i] = 0; // All tokens are segment A (no second sequence)
                }

                // 3. Run inference
                var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask), NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds) };

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
                float[] output = results.First().AsEnumerable<float>().ToArray();

                // 4. Mean pooling (average embeddings across tokens)
                float[] embeddings = MeanPooling(output, tokens.Length);

                // 5. Normalize
                return Normalize(embeddings);
            },
            cancellationToken
        );
    }

    public async Task<List<float[]>> CreateBatchEmbeddingsAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var embeddings = new List<float[]>();

        foreach (string text in texts)
        {
            float[] embedding = await CreateEmbeddingAsync(text, cancellationToken);
            embeddings.Add(embedding);
        }

        return embeddings;
    }

    private static float[] MeanPooling(float[] embeddings, int tokenCount)
    {
        int dimensions = embeddings.Length / tokenCount;
        float[] pooled = new float[dimensions];

        for (int i = 0; i < tokenCount; i++)
        {
            for (int d = 0; d < dimensions; d++)
            {
                pooled[d] += embeddings[(i * dimensions) + d];
            }
        }

        for (int d = 0; d < dimensions; d++)
        {
            pooled[d] /= tokenCount;
        }

        return pooled;
    }

    private static float[] Normalize(float[] vector)
    {
        float norm = MathF.Sqrt(vector.Sum(x => x * x));
        return vector.Select(x => x / norm).ToArray();
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    /// <summary>
    /// BERT tokenizer using actual vocab.txt file
    /// Much better than hash-based tokenization!
    /// </summary>
    private sealed class SimpleTokenizer
    {
        // The model reads up to 256 word pieces: a passage of a Knowledge Base (about 900 characters) is read whole.
        private const int MaxLength = 256;
        private readonly Dictionary<string, int> _vocab;
        private readonly int _clsTokenId;
        private readonly int _sepTokenId;
        private readonly int _unkTokenId;
        private readonly ILogger? _logger;

        public SimpleTokenizer(string vocabPath, ILogger? logger = null)
        {
            _logger = logger;
            _vocab = [];

            if (!File.Exists(vocabPath))
            {
                throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");
            }

            // Load vocabulary
            string[] lines = File.ReadAllLines(vocabPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string token = lines[i].Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    _vocab[token] = i;
                }
            }

            // Get special token IDs
            _clsTokenId = _vocab.TryGetValue("[CLS]", out int cls) ? cls : 101;
            _sepTokenId = _vocab.TryGetValue("[SEP]", out int sep) ? sep : 102;
            _unkTokenId = _vocab.TryGetValue("[UNK]", out int unk) ? unk : 100;

            _logger?.LogInformation("Loaded BERT vocabulary with {Count} tokens", _vocab.Count);
        }

        /// <summary>
        /// The word pieces of a text as the model reads them (WordPiece, lower case, accents removed): a word missing from the
        /// vocabulary is cut into the longest pieces it has ("laptops" is "laptop" + "##s"), only an impossible word is [UNK].
        /// No padding: the mean of the vectors is taken over the text itself, up to <see cref="MaxLength"/> pieces.
        /// </summary>
        public long[] Tokenize(string text)
        {
            var tokens = new List<long>(MaxLength) { _clsTokenId };
            foreach (var word in Words(text))
            {
                foreach (var piece in Pieces(word))
                {
                    if (tokens.Count == MaxLength - 1)
                        break;
                    tokens.Add(piece);
                }

                if (tokens.Count == MaxLength - 1)
                    break;
            }

            tokens.Add(_sepTokenId);
            return [.. tokens];
        }

        // Words and punctuation apart, lower case, without accents: what the uncased model was trained on.
        private static IEnumerable<string> Words(string text)
        {
            var plain = new System.Text.StringBuilder(text.Length);
            foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    plain.Append(char.ToLowerInvariant(c));
            }

            var word = new System.Text.StringBuilder();
            foreach (var c in plain.ToString())
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    if (word.Length > 0)
                        yield return word.ToString();
                    word.Clear();
                }
                else if (char.IsPunctuation(c) || char.IsSymbol(c))
                {
                    if (word.Length > 0)
                        yield return word.ToString();
                    word.Clear();
                    yield return c.ToString();
                }
                else
                {
                    word.Append(c);
                }
            }

            if (word.Length > 0)
                yield return word.ToString();
        }

        // Greedy longest-match-first, as WordPiece does.
        private IEnumerable<long> Pieces(string word)
        {
            if (word.Length > 100)
                return [_unkTokenId];
            var pieces = new List<long>();
            var start = 0;
            while (start < word.Length)
            {
                var end = word.Length;
                var found = -1;
                while (start < end)
                {
                    var candidate = (start > 0 ? "##" : "") + word[start..end];
                    if (_vocab.TryGetValue(candidate, out var id))
                    {
                        found = id;
                        break;
                    }

                    end--;
                }

                if (found < 0)
                    return [_unkTokenId];
                pieces.Add(found);
                start = end;
            }

            return pieces;
        }
    }
}

/// <summary>
/// Factory for creating local ONNX embedding capabilities.
/// No credentials needed - 100% local!
/// </summary>
public sealed class OnnxLocalEmbeddingCapabilityFactory : IResourceCapabilityFactory<IEmbeddingCapability>
{
    private readonly ILogger<OnnxLocalEmbeddingCapability>? _logger;
    private readonly string _modelPath;

    public Type CapabilityType => typeof(IEmbeddingCapability);

    public OnnxLocalEmbeddingCapabilityFactory(string modelPath, ILogger<OnnxLocalEmbeddingCapability>? logger = null)
    {
        _modelPath = modelPath;
        _logger = logger;
    }

    public ValueTask<IEmbeddingCapability> CreateAsync(ResourceBinding binding, NodeConfig context, CancellationToken cancellationToken = default)
    {
        var capability = new OnnxLocalEmbeddingCapability(_modelPath, _logger);
        return ValueTask.FromResult<IEmbeddingCapability>(capability);
    }

    async ValueTask<IResourceCapability> IResourceCapabilityFactory.CreateAsync(ResourceBinding binding, NodeConfig context, CancellationToken cancellationToken)
    {
        return await CreateAsync(binding, context, cancellationToken);
    }
}
