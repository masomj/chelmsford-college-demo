using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure;
using Chelmsford.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chelmsford.Tests;

public sealed class PaymentGatewaySelectionTests
{
    private static (IPaymentGateway Gateway, List<(LogLevel Level, string Message)> Logs) Resolve(Dictionary<string, string?> settings)
    {
        var logs = new List<(LogLevel, string)>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(logs)));
        services.AddChelmsfordInfrastructure(configuration, Path.GetTempPath());

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IPaymentGateway>(), logs);
    }

    [Fact]
    public void No_key_uses_the_demo_checkout()
    {
        var (gateway, _) = Resolve([]);

        Assert.IsType<DemoPaymentGateway>(gateway);
    }

    [Fact]
    public void A_test_key_uses_Stripe()
    {
        var (gateway, _) = Resolve(new() { ["Payments:Stripe:SecretKey"] = "sk_test_abc" });

        Assert.IsType<StripePaymentGateway>(gateway);
        Assert.True(gateway.IsTestMode);
    }

    [Fact]
    public void A_live_key_is_refused_with_an_error_and_the_demo_checkout_is_used()
    {
        var (gateway, logs) = Resolve(new() { ["Payments:Stripe:SecretKey"] = "sk_live_abc" });

        Assert.IsType<DemoPaymentGateway>(gateway);
        var error = Assert.Single(logs, l => l.Level == LogLevel.Error);
        Assert.Contains("AllowLiveKeys", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk_live_abc", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    public void A_live_key_is_refused_unless_live_keys_are_explicitly_allowed(string allow)
    {
        var (gateway, _) = Resolve(new() { ["Payments:Stripe:SecretKey"] = "sk_live_abc", ["Payments:AllowLiveKeys"] = allow });

        Assert.IsType<DemoPaymentGateway>(gateway);
    }

    [Fact]
    public void A_live_key_is_used_when_live_keys_are_allowed()
    {
        var (gateway, logs) = Resolve(new() { ["Payments:Stripe:SecretKey"] = "sk_live_abc", ["Payments:AllowLiveKeys"] = "true" });

        Assert.IsType<StripePaymentGateway>(gateway);
        Assert.False(gateway.IsTestMode);
        Assert.DoesNotContain(logs, l => l.Level == LogLevel.Error);
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel, string)> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<(LogLevel, string)> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                logs.Add((logLevel, formatter(state, exception)));
        }
    }
}
