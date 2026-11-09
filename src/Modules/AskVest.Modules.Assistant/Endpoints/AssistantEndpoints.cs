using System.Text.Json;
using AskVest.Modules.Assistant.Domain;
using AskVest.Modules.Assistant.Services;
using AskVest.Modules.Assistant.Tools;
using AskVest.Modules.Compliance.Contracts;
using AskVest.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AskVest.Modules.Assistant.Endpoints;

public static class AssistantEndpoints
{
    public static void Map(RouteGroupBuilder api, bool probesEnabled)
    {
        var group = api.MapGroup("/assistant")
            .RequireAuthorization(CompliancePolicyNames.Assistant)
            .WithTags("Assistant");

        group.MapPost("/messages", SendMessageAsync)
            .WithName("SendAssistantMessage")
            .WithSummary("Send a message to the assistant. Returns a cited, policy-checked answer with server-built fact cards.")
            .WithMetadata(new RequiresIdempotencyAttribute());

        group.MapGet("/conversations", ListConversationsAsync)
            .WithName("ListAssistantConversations")
            .WithSummary("The caller's assistant conversations, newest first.");

        group.MapGet("/conversations/{conversationId:guid}", GetConversationAsync)
            .WithName("GetAssistantConversation")
            .WithSummary("One of the caller's conversations with its messages in order.");

        group.MapDelete("/conversations/{conversationId:guid}", DeleteConversationAsync)
            .WithName("DeleteAssistantConversation")
            .WithSummary("Delete a conversation and its messages. Audit events remain. Exempt from idempotency storage: deleting twice is harmless because a missing conversation answers resource.not_found.")
            .WithMetadata(new IdempotencyExemptAttribute());

        if (probesEnabled)
        {
            api.MapPost("/diagnostics/assistant-scenario", ScenarioProbeAsync)
                .WithName("AssistantScenarioProbe")
                .WithSummary("Diagnostic: set the fake assistant model scenario.")
                .WithTags("Diagnostics")
                .ExcludeFromDescription()
                .WithMetadata(new IdempotencyExemptAttribute());
        }
    }

    private static async Task<IResult> SendMessageAsync(
        HttpContext httpContext,
        AssistantMessageRequest request,
        AssistantChatService chat,
        CancellationToken cancellationToken)
    {
        var outcome = await chat.SendMessageAsync(
            CurrentUserId(httpContext),
            request.ConversationId,
            request.Text,
            httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty,
            cancellationToken);

        if (outcome.Result.IsFailure)
        {
            if (outcome.RetryAfterSeconds > 0)
            {
                httpContext.Response.Headers.RetryAfter = outcome.RetryAfterSeconds.ToString();
            }

            return AssistantResults.Error(outcome.Result.Error!, httpContext);
        }

        var reply = outcome.Result.Value!;
        return AssistantResults.Ok(
            new
            {
                conversationId = reply.ConversationId,
                messageId = reply.MessageId,
                text = reply.Text,
                citations = reply.Citations,
                factCards = reply.FactCards,
                actions = reply.Actions.Select(action => new
                {
                    type = action.Type,
                    payload = JsonSerializer.Deserialize<JsonElement>(action.PayloadJson),
                }),
                policy = new { version = reply.PolicyVersion, blocked = reply.PolicyBlocked },
                disclosure = Disclosure(httpContext),
            },
            "Assistant replied.",
            httpContext);
    }

    private static async Task<IResult> ListConversationsAsync(
        HttpContext httpContext,
        AssistantChatService chat,
        int? limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var page = await chat.ListConversationsAsync(
            CurrentUserId(httpContext),
            Math.Clamp(limit ?? PageRequest.DefaultLimit, 1, PageRequest.MaxLimit),
            cursor,
            cancellationToken);
        return AssistantResults.Ok(
            new
            {
                items = page.Items.Select(item => new
                {
                    id = item.Id,
                    title = item.Title,
                    createdAt = item.CreatedAt,
                    updatedAt = item.UpdatedAt,
                }),
                nextCursor = page.NextCursor,
                hasMore = page.HasMore,
                limit = page.Limit,
            },
            "Conversations loaded.",
            httpContext);
    }

    private static async Task<IResult> GetConversationAsync(
        HttpContext httpContext,
        AssistantChatService chat,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var detail = await chat.GetConversationAsync(
            CurrentUserId(httpContext), conversationId, cancellationToken);
        if (detail is null)
        {
            return AssistantResults.Error(
                new Error(ErrorCodes.ResourceNotFound, "The conversation was not found.", ErrorType.NotFound),
                httpContext);
        }

        return AssistantResults.Ok(
            new
            {
                id = detail.Id,
                title = detail.Title,
                createdAt = detail.CreatedAt,
                updatedAt = detail.UpdatedAt,
                messages = detail.Messages.Select(message => new
                {
                    id = message.Id,
                    role = message.Role,
                    text = message.Text,
                    citations = message.Citations,
                    factCards = message.FactCards,
                    actions = message.Actions,
                    policyOutcome = message.PolicyOutcome,
                    createdAt = message.CreatedAt,
                }),
            },
            "Conversation loaded.",
            httpContext);
    }

    private static async Task<IResult> DeleteConversationAsync(
        HttpContext httpContext,
        AssistantChatService chat,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var deleted = await chat.DeleteConversationAsync(
            CurrentUserId(httpContext), conversationId, cancellationToken);
        return deleted
            ? AssistantResults.Ok(new { id = conversationId }, "Conversation deleted.", httpContext)
            : AssistantResults.Error(
                new Error(ErrorCodes.ResourceNotFound, "The conversation was not found.", ErrorType.NotFound),
                httpContext);
    }

    private static IResult ScenarioProbeAsync(
        HttpContext httpContext,
        AssistantScenarioProbeRequest request,
        Contracts.ILanguageModelClient model)
    {
        if (model is not FakeLanguageModelClient fake)
        {
            return AssistantResults.Ok(
                new { applied = false },
                "Probe ignored: the configured model is not the fake.",
                httpContext);
        }

        var scenario = request.Scenario?.Trim() ?? string.Empty;
        if (!AssistantScenarios.All.Contains(scenario))
        {
            return AssistantResults.Error(
                new Error(ErrorCodes.ValidationFailed, "The scenario is not recognized.", ErrorType.Validation),
                httpContext);
        }

        fake.Scenario = scenario;
        return AssistantResults.Ok(
            new { applied = true, scenario },
            "Scenario set.",
            httpContext);
    }

    private static string Disclosure(HttpContext httpContext)
        => httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.AssistantOptions>>()
            .Value.DisclosureLine;

    private static Guid CurrentUserId(HttpContext httpContext)
        => Guid.Parse(httpContext.User.FindFirst("sub")!.Value);

    private sealed record AssistantMessageRequest(Guid? ConversationId, string? Text);

    private sealed record AssistantScenarioProbeRequest(string? Scenario);
}
