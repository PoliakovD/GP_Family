namespace FamilyHub.Contracts.Messaging;

/// <summary>
/// Consumer group'ы, общие между процессами (сейчас — FamilyHub.Api и FamilyHub.TelegramBot),
/// чтобы имя не разъехалось между продюсером топологии и тестами. Группы, приватные для Api
/// (notifications-*/medical-*), остаются inline-строками в Program.cs — сюда выносятся только
/// те, что нужно видеть больше чем одному процессу.
/// </summary>
public static class KafkaConsumerGroups
{
    /// <summary>FamilyHub.TelegramBot, единственный потребитель топика TelegramOutbound.</summary>
    public const string TelegramBotOutbound = "bot-telegram-outbound";
}
