using FamilyHub.Contracts.Events;
using FamilyHub.TelegramBot.Configuration;
using FamilyHub.TelegramBot.Messaging;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Xunit;

namespace FamilyHub.UnitTests.TelegramBot;

/// <summary>
/// Замена части покрытия TelegramNotificationSenderTests, которая раньше проверяла построение
/// WebApp-кнопки и обработку ошибок Bot API — после выноса бота (ADR-0008) эта логика живёт
/// здесь, а не в TelegramOutboundPublisher (см. TelegramOutboundPublisherTests в Infrastructure).
/// InMemory MassTransit-харнесс — консьюмеру всё равно, откуда пришло сообщение (топология Kafka
/// Rider покрыта отдельно, см. KafkaBridgeFlowTests в IntegrationTests).
/// </summary>
public class TelegramOutboundConsumerTests
{
    private static async Task<(ITelegramBotClient Bot, ServiceProvider Provider, ITestHarness Harness)> StartHarnessAsync(string miniAppUrl)
    {
        var bot = Substitute.For<ITelegramBotClient>();
        bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new Message());

        var provider = new ServiceCollection()
            .AddSingleton(bot)
            .AddSingleton<IOptions<BotOptions>>(Options.Create(new BotOptions { MiniAppUrl = miniAppUrl }))
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<TelegramOutboundConsumer>();
                x.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            })
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        return (bot, provider, harness);
    }

    [Fact]
    public async Task Consume_WithMiniAppUrlConfigured_SendsMessageWithWebAppButton()
    {
        var (bot, provider, harness) = await StartHarnessAsync("https://mini.example.test");
        await using var _ = provider;
        try
        {
            await harness.Bus.Publish(new TelegramMessageRequestedEvent(12345, "Заголовок\n\nТело", WithMiniAppButton: true, "dk-1"));
            (await harness.Consumed.Any<TelegramMessageRequestedEvent>()).Should().BeTrue();

            await bot.Received(1).SendRequest(
                Arg.Is<SendMessageRequest>(r => r.ChatId == 12345 && r.Text == "Заголовок\n\nТело"
                    && r.ReplyMarkup is Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consume_WithoutMiniAppUrl_SendsMessageWithoutButton()
    {
        var (bot, provider, harness) = await StartHarnessAsync(miniAppUrl: "");
        await using var _ = provider;
        try
        {
            await harness.Bus.Publish(new TelegramMessageRequestedEvent(999, "Текст", WithMiniAppButton: true, "dk-2"));
            (await harness.Consumed.Any<TelegramMessageRequestedEvent>()).Should().BeTrue();

            await bot.Received(1).SendRequest(
                Arg.Is<SendMessageRequest>(r => r.ChatId == 999 && r.ReplyMarkup == null),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Consume_BotBlockedByUser_SwallowsAndDoesNotFault()
    {
        var (bot, provider, harness) = await StartHarnessAsync("https://mini.example.test");
        await using var _ = provider;
        bot.SendRequest(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiRequestException("Forbidden: bot was blocked by the user", 403));
        try
        {
            await harness.Bus.Publish(new TelegramMessageRequestedEvent(1, "Текст", WithMiniAppButton: false, "dk-3"));

            (await harness.Consumed.Any<TelegramMessageRequestedEvent>()).Should().BeTrue();
            var consumerHarness = provider.GetRequiredService<IConsumerTestHarness<TelegramOutboundConsumer>>();
            (await consumerHarness.Consumed.Any<TelegramMessageRequestedEvent>()).Should().BeTrue(
                "403 — перманентная ошибка, потребитель должен проглотить её, а не dead-letter'ить");
        }
        finally
        {
            await harness.Stop();
        }
    }
}
