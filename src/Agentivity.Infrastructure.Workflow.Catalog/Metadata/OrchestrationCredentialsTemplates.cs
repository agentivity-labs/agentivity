using Agentivity.Domain.Credentials;
using Agentivity.Infrastructure.Workflow.Services.CredentialsManagement;

namespace Agentivity.Infrastructure.Workflow.Catalog.Metadata;

/// <summary>
/// Registers all credential templates used by workflow nodes.
/// Call RegisterAll() during application startup.
/// </summary>
public static class OrchestrationCredentialsTemplates
{
    /// <summary>
    /// Registers all credential templates.
    /// </summary>
    public static void RegisterAll()
    {
        RegisterOpenAiTemplate();
        RegisterAnthropicTemplate();
        RegisterDeepSeekTemplate();
        RegisterXAiTemplate();
        RegisterGeminiTemplate();
        RegisterSmtpTemplate();
        RegisterImapTemplate();
        RegisterSlackTemplate();
        RegisterTavilyTemplate();
        RegisterSerpApiTemplate();
        RegisterSearXngTemplate();
        RegisterFirecrawlTemplate();
        RegisterWordPressTemplate();
        RegisterOpenAiImageTemplate();
        RegisterSupabaseTemplate();
        RegisterQdrantTemplate();
        RegisterPineconeTemplate();
        RegisterAwsBedrockTemplate();
        RegisterCohereTemplate();
    }

    private static void RegisterCohereTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "cohere",
                Category = CredentialsCategories.AiModels,
                DisplayName = "Cohere",
                Description = "Credentials for Cohere's API (rerank, embeddings, Command R+ chat).",
                Icon = 0xec35, // smart_toy
                IconColor = "#FF39594D", // Cohere dark green
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        Placeholder = "https://api.cohere.com/v2",
                        HelpText = "Leave empty to use Cohere's default API endpoint.",
                    },
                ],
            }
        );
    }

    private static void RegisterAwsBedrockTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "aws_bedrock",
                Category = CredentialsCategories.AiModels,
                DisplayName = "AWS Bedrock",
                Description = "AWS credentials used to sign Bedrock InvokeModel requests (SigV4). Used by the AWS Bedrock LLM model node.",
                Icon = 0xec35, // smart_toy
                IconColor = "#FFFF9900", // AWS orange
                Fields =
                [
                    new()
                    {
                        Key = "access_key_id",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "Access Key ID",
                        Placeholder = "AKIA...",
                    },
                    new()
                    {
                        Key = "secret_access_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "Secret Access Key",
                    },
                    new()
                    {
                        Key = "region",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Region",
                        Placeholder = "us-east-1",
                        HelpText = "The AWS region where Bedrock model access is enabled for this account.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The Bedrock model ID, exactly as the AWS console shows it under Model access (Bedrock gives no list to choose from). A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterPineconeTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "pinecone",
                Category = CredentialsCategories.Data,
                DisplayName = "Pinecone",
                Description = "Connection settings for a Pinecone index. Used by the Pinecone vector-store memory node.",
                Icon = 0xe1db, // storage
                IconColor = "#FF1A1A2E", // Pinecone dark navy
                Fields =
                [
                    new()
                    {
                        Key = "host",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Index host",
                        Placeholder = "my-index-abc123.svc.us-east-1-aws.pinecone.io",
                        HelpText = "The index's own host, from the Pinecone console (Database > your index > Host). Not the generic api.pinecone.io endpoint.",
                    },
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "pcsk_...",
                    },
                ],
            }
        );
    }

    private static void RegisterQdrantTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "qdrant",
                Category = CredentialsCategories.Data,
                DisplayName = "Qdrant",
                Description = "Connection settings for a Qdrant vector database instance (self-hosted or Qdrant Cloud). Used by vector-store memory nodes.",
                Icon = 0xe1db, // storage
                IconColor = "#FFDC244C", // Qdrant red
                Fields =
                [
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Base URL",
                        Placeholder = "http://localhost:6333",
                        HelpText = "Qdrant REST API base URL (self-hosted) or cluster URL (Qdrant Cloud).",
                    },
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = false,
                        Label = "API Key",
                        HelpText = "Required for Qdrant Cloud. Leave empty for a local instance with no auth.",
                    },
                ],
            }
        );
    }

    private static void RegisterFirecrawlTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "firecrawl",
                Category = CredentialsCategories.Web,
                DisplayName = "Firecrawl",
                Description = "Credentials for Firecrawl web scraping. Used by Firecrawl scraper nodes and AI scraper tools.",
                Icon = 0xe894,
                IconColor = "#FFEF6C00",
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "fc-...",
                    },
                ],
            }
        );
    }

    private static void RegisterTavilyTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "tavily",
                Category = CredentialsCategories.Web,
                DisplayName = "Tavily",
                Description = "Credentials for Tavily web search. Used by Tavily web search nodes and AI search tools.",
                Icon = 0xe8b6,
                IconColor = "#FF4CAF50",
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "tvly-...",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.tavily.com/search",
                    },
                ],
            }
        );
    }

    private static void RegisterSerpApiTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "serpapi",
                Category = CredentialsCategories.Web,
                DisplayName = "SerpApi",
                Description = "Credentials for SerpApi web search. Used by SerpApi web search nodes and AI search tools.",
                Icon = 0xe8b6,
                IconColor = "#FFFF9800",
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://serpapi.com/search.json",
                    },
                ],
            }
        );
    }

    private static void RegisterSearXngTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "searxng",
                Category = CredentialsCategories.Web,
                DisplayName = "SearXNG",
                Description = "Connection settings for a SearXNG instance. Used by SearXNG web search nodes and AI search tools.",
                Icon = 0xe8b6,
                IconColor = "#FF26A69A",
                Fields =
                [
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Base URL",
                        Placeholder = "https://searx.example.com",
                        HelpText = "Base URL of the SearXNG instance. The node appends '/search' automatically when needed.",
                    },
                ],
            }
        );
    }

    private static void RegisterSmtpTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "smtp",
                Category = CredentialsCategories.Messaging,
                DisplayName = "SMTP Email",
                Description = "Credentials for sending emails through an SMTP server. Used by direct email workflow nodes and email interaction channels.",
                Icon = 0xe0be,
                IconColor = "#FF2196F3",
                Fields =
                [
                    new()
                    {
                        Key = "host",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "SMTP Host",
                        Placeholder = "smtp.example.com",
                    },
                    new()
                    {
                        Key = "port",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "SMTP Port",
                        DefaultValue = "587",
                    },
                    new()
                    {
                        Key = "username",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Username",
                    },
                    new()
                    {
                        Key = "password",
                        Type = CredentialsFieldType.Secret,
                        Required = false,
                        Label = "Password",
                    },
                    new()
                    {
                        Key = "from_email",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "From Email",
                        Placeholder = "noreply@example.com",
                    },
                    new()
                    {
                        Key = "from_name",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "From Name",
                        DefaultValue = "Agentivity",
                    },
                    new()
                    {
                        Key = "security",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Security (auto, ssl, starttls or none)",
                        Placeholder = "auto",
                        DefaultValue = "auto",
                    },
                    new()
                    {
                        Key = "enable_ssl",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Enable SSL",
                        DefaultValue = "true",
                    },
                ],
            }
        );
    }

    private static void RegisterImapTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "imap",
                Category = CredentialsCategories.Messaging,
                DisplayName = "Email Inbox (IMAP)",
                Description = "A mailbox to read through IMAP: the standard way to read e-mail, offered by nearly every provider (Gmail, Outlook, OVH, Infomaniak…). Used by the nodes and the agent tool that read, answer and file e-mails. Many providers want an app password rather than the account's own.",
                Icon = 0xe156,
                IconColor = "#FF2196F3",
                Fields =
                [
                    new()
                    {
                        Key = "host",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "IMAP Host",
                        Placeholder = "imap.example.com",
                    },
                    new()
                    {
                        Key = "port",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "IMAP Port",
                        DefaultValue = "993",
                    },
                    new()
                    {
                        Key = "username",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Username",
                        Placeholder = "me@example.com",
                    },
                    new()
                    {
                        Key = "password",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "Password (or app password)",
                    },
                    new()
                    {
                        Key = "security",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Security (auto, ssl, starttls or none)",
                        Placeholder = "auto",
                        DefaultValue = "auto",
                    },
                ],
            }
        );
    }

    private static void RegisterSlackTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "slack",
                Category = CredentialsCategories.Messaging,
                DisplayName = "Slack",
                Description = "Credentials for Slack bot messaging. Used by Slack interaction channels and future Slack workflow nodes.",
                Icon = 0xe7ef,
                IconColor = "#FF4A154B",
                Fields =
                [
                    new()
                    {
                        Key = "bot_token",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "Bot Token",
                        Placeholder = "xoxb-...",
                        HelpText = "Slack bot token with permission to post messages to the target channel.",
                    },
                    new()
                    {
                        Key = "signing_secret",
                        Type = CredentialsFieldType.Secret,
                        Required = false,
                        Label = "Signing Secret",
                        HelpText = "Slack signing secret used to validate inbound webhook events for true human-in-the-loop responses.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://slack.com/api/",
                        HelpText = "Slack Web API base URL. Keep the default unless using a compatible proxy.",
                    },
                ],
            }
        );
    }

    private static void RegisterOpenAiTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "openai",
                Category = CredentialsCategories.AiModels,
                DisplayName = "OpenAI",
                Description = "Credentials for OpenAI models (GPT-4o, o3, etc.) and OpenAI-compatible endpoints (LM Studio, Ollama, Groq, Azure OpenAI…).",
                Icon = 0xec35, // smart_toy
                IconColor = "#FF10A37F", // OpenAI green
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = false,
                        Label = "API Key",
                        Placeholder = "sk-...",
                        HelpText = "Your OpenAI API key. Leave empty for local servers like LM Studio or Ollama.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.openai.com/v1",
                        HelpText = "API endpoint URL. Change for Azure OpenAI or local servers.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The model this connection uses, exactly as the provider names it: choose it in the list the provider gives once the key is entered. A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterAnthropicTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "anthropic",
                Category = CredentialsCategories.AiModels,
                DisplayName = "Anthropic",
                Description = "Credentials for Anthropic Claude models (claude-opus-4, claude-sonnet-4, etc.).",
                Icon = 0xec35, // smart_toy
                IconColor = "#FFD4A27F", // Anthropic warm tone
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "sk-ant-...",
                        HelpText = "Your Anthropic API key from console.anthropic.com.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.anthropic.com",
                        HelpText = "The address of the Anthropic API. Keep it unless you go through a proxy, or Anthropic moves it.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The model this connection uses, exactly as the provider names it: choose it in the list the provider gives once the key is entered. A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterDeepSeekTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "deepseek",
                Category = CredentialsCategories.AiModels,
                DisplayName = "DeepSeek",
                Description = "Credentials for DeepSeek AI models. Uses an OpenAI-compatible API — works with any LLM node.",
                Icon = 0xec35, // smart_toy
                IconColor = "#FF1A73E8", // blue
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "sk-...",
                        HelpText = "Your DeepSeek API key from platform.deepseek.com.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.deepseek.com/v1",
                        HelpText = "DeepSeek API endpoint. Leave as default unless using a proxy.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The model this connection uses, exactly as the provider names it: choose it in the list the provider gives once the key is entered. A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterXAiTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "xai",
                Category = CredentialsCategories.AiModels,
                DisplayName = "xAI / Grok",
                Description = "Credentials for xAI Grok models. Uses an OpenAI-compatible API — works with any LLM node.",
                Icon = 0xec35, // smart_toy
                IconColor = "#FF000000", // black
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "xai-...",
                        HelpText = "Your xAI API key from console.x.ai.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.x.ai/v1",
                        HelpText = "xAI API endpoint. Leave as default unless using a proxy.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The model this connection uses, exactly as the provider names it: choose it in the list the provider gives once the key is entered. A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterGeminiTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "gemini",
                Category = CredentialsCategories.AiModels,
                DisplayName = "Google Gemini",
                Description = "Credentials for Google Gemini models via AI Studio. The Gemini API format is handled automatically.",
                Icon = 0xec35, // smart_toy
                IconColor = "#FF4285F4", // Google blue
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "AIza...",
                        HelpText = "Your Google AI Studio API key from aistudio.google.com.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://generativelanguage.googleapis.com/v1beta",
                        HelpText = "Gemini API endpoint. Leave as default for Google AI Studio.",
                    },
                    new()
                    {
                        Key = "model",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Model",
                        HelpText = "The model this connection uses, exactly as the provider names it: choose it in the list the provider gives once the key is entered. A step can still choose another one.",
                    },
                ],
            }
        );
    }

    private static void RegisterWordPressTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "wordpress",
                Category = CredentialsCategories.Publishing,
                DisplayName = "WordPress",
                Description = "Credentials for WordPress sites using the REST API. Requires an Application Password for authentication. Used by nodes that publish posts, upload media, or manage WordPress content.",
                Icon = 0xef42, // article
                IconColor = "#FF2196F3", // blue
                Fields =
                [
                    new()
                    {
                        Key = "url",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Site URL",
                        Placeholder = "https://example.com/wp-json/wp/v2/",
                        HelpText = "WordPress REST API endpoint. Usually your-site.com/wp-json/wp/v2/",
                    },
                    new()
                    {
                        Key = "username",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Username",
                        HelpText = "WordPress username with publishing permissions.",
                    },
                    new()
                    {
                        Key = "app_password",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "Application Password",
                        HelpText = "Generate in WordPress: Users > Your Profile > Application Passwords.",
                    },
                ],
            }
        );
    }

    private static void RegisterOpenAiImageTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "openai_image",
                Category = CredentialsCategories.AiModels,
                DisplayName = "OpenAI Image",
                Description = "Credentials for OpenAI's image generation API (DALL-E, GPT-Image). Used by nodes that generate images from text prompts.",
                Icon = 0xe3f4, // image
                IconColor = "#FF4CAF50", // green
                Fields =
                [
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key",
                        Placeholder = "sk-...",
                        HelpText = "Your OpenAI API key with image generation access.",
                    },
                    new()
                    {
                        Key = "base_url",
                        Type = CredentialsFieldType.Text,
                        Required = false,
                        Label = "Base URL",
                        DefaultValue = "https://api.openai.com/v1",
                        HelpText = "API endpoint URL. Usually the default OpenAI endpoint.",
                    },
                ],
            }
        );
    }

    private static void RegisterSupabaseTemplate()
    {
        CredentialsTemplateRegistry.Register(
            new CredentialsTemplate
            {
                Id = "supabase",
                Category = CredentialsCategories.Data,
                DisplayName = "Supabase",
                Description = "Credentials for Supabase cloud platform. Provides access to PostgreSQL database, storage, and authentication APIs.",
                Icon = 0xe1db, // storage
                IconColor = "#FF00C853", // green
                Fields =
                [
                    new()
                    {
                        Key = "supabase_url",
                        Type = CredentialsFieldType.Text,
                        Required = true,
                        Label = "Supabase URL",
                        Placeholder = "https://xxxxx.supabase.co",
                        HelpText = "Your Supabase project URL from project settings.",
                    },
                    new()
                    {
                        Key = "api_key",
                        Type = CredentialsFieldType.Secret,
                        Required = true,
                        Label = "API Key (anon/public)",
                        Placeholder = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
                        HelpText = "Your Supabase anon/public API key from project settings.",
                    },
                ],
            }
        );
    }
}
