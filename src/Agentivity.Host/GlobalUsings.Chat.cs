// Thread model — now generic, lives in Agentivity.Infrastructure.Interactions
global using ChatAgentToken = Agentivity.Infrastructure.Interactions.Chat.Services.ChatAgentToken;
global using ChatCreateThreadRequest = Agentivity.Infrastructure.Interactions.Chat.Services.ChatCreateThreadRequest;
global using ChatMessageRequest = Agentivity.Infrastructure.Interactions.Chat.Services.ChatMessageRequest;
global using ChatPostMessageResult = Agentivity.Infrastructure.Interactions.Chat.Services.ChatPostMessageResult;
global using ChatResumedInteractionResult = Agentivity.Infrastructure.Interactions.Chat.Services.ChatResumedInteractionResult;
// Chat-specific services
global using ChatService = Agentivity.Infrastructure.Interactions.Chat.Services.ChatService;
global using IChatAgentInvoker = Agentivity.Infrastructure.Interactions.Chat.Services.IChatAgentInvoker;
global using IInteractionThreadStore = Agentivity.Infrastructure.Interactions.IInteractionThreadStore;
global using InteractionThread = Agentivity.Infrastructure.Interactions.InteractionThread;
global using InteractionThreadStatus = Agentivity.Infrastructure.Interactions.InteractionThreadStatus;
global using ThreadAuthorType = Agentivity.Infrastructure.Interactions.ThreadAuthorType;
global using ThreadMessage = Agentivity.Infrastructure.Interactions.ThreadMessage;
