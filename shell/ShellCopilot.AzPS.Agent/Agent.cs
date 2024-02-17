﻿using Azure.Identity;
using ShellCopilot.Abstraction;

namespace ShellCopilot.AzPS.Agent;

public sealed class AzPSAgent : ILLMAgent
{
    public string Name => "az-ps";
    public string Description => "An AI assistant to provide Azure PowerShell scripts or commands for managing Azure resources and end-to-end scenarios that involve multiple Azure resources.";
    public Dictionary<string, string> AgentInfo { private set; get; } = null;
    public string SettingFile { private set; get; } = null;

    private const string SettingFileName = "az-ps.agent.json";

    private string _configRoot;
    private RenderingStyle _renderingStyle;
    private ChatService _chatService;

    public void Dispose()
    {
        _chatService.Dispose();
    }

    public void Initialize(AgentConfig config)
    {
        _renderingStyle = config.RenderingStyle;
        _configRoot = config.ConfigurationRoot;
        SettingFile = Path.Combine(_configRoot, SettingFileName);

        string tenantId = null;
        if (config.Context is not null)
        {
            config.Context.TryGetValue("tenant", out tenantId);
            config.Context.TryGetValue("subscription", out string subscriptionId);

            AgentInfo = new Dictionary<string, string>
            {
                ["Tenant"] = tenantId,
                ["Subscription"] = subscriptionId,
            };
        }

        _chatService = new ChatService(config.IsInteractive, tenantId);
    }

    public IEnumerable<CommandBase> GetCommands() => null;
    public bool CanAcceptFeedback(UserAction action) => false;
    public void OnUserAction(UserActionPayload actionPayload) {}

    public async Task<bool> Chat(string input, IShell shell)
    {
        IHost host = shell.Host;
        CancellationToken token = shell.CancellationToken;

        try
        {
            // The AzPS endpoint can return status information in the streaming manner, so we can
            // update the status message while waiting for the answer payload to come back.
            using ChunkReader chunkReader = await host.RunWithSpinnerAsync(
                status: "Thinking ...",
                func: async context => await _chatService.GetStreamingChatResponseAsync(context, input, token)
            ).ConfigureAwait(false);

            if (chunkReader is null)
            {
                // Operation was cancelled by user.
                return true;
            }

            using var streamingRender = host.NewStreamRender(token);

            try
            {
                while (true)
                {
                    ChunkData chunk = await chunkReader.ReadChunkAsync(token).ConfigureAwait(false);
                    if (chunk is null || chunk.Status.Equals("Finished Generate Answer", StringComparison.Ordinal))
                    {
                        break;
                    }

                    streamingRender.Refresh(chunk.Message);
                }
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled by user.
            }

            _chatService.AddResponseToHistory(streamingRender.AccumulatedContent);
        }
        catch (RefreshTokenException ex)
        {
            Exception inner = ex.InnerException;
            if (inner is CredentialUnavailableException)
            {
                host.MarkupErrorLine($"Access token not available. Query cannot be served.");
                host.MarkupErrorLine($"The '{Name}' agent depends on the Azure PowerShell credential to acquire access token. Please run 'Connect-AzAccount' from a command-line shell to setup account.");
            }
            else
            {
                host.MarkupErrorLine($"Failed to get the access token. {inner.Message}");
            }

            return false;
        }

        return true;
    }
}
