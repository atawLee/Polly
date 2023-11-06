﻿using System.Net;
using System.Net.Http;
using Polly.CircuitBreaker;
using Snippets.Docs.Utils;

namespace Snippets.Docs;

internal static class CircuitBreaker
{
    public static async Task Usage()
    {
        #region circuit-breaker

        // Circuit breaker with default options.
        // See https://www.pollydocs.org/strategies/circuit-breaker#defaults for defaults.
        var optionsDefaults = new CircuitBreakerStrategyOptions();

        // Circuit breaker with customized options:
        // The circuit will break if more than 50% of actions result in handled exceptions,
        // within any 10-second sampling duration, and at least 8 actions are processed.
        var optionsComplex = new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 8,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder().Handle<SomeExceptionType>()
        };

        // Adds a circuit breaker with a dynamic break duration:
        //
        // Same circuit breaking conditions as above, but with a dynamic break duration based on the failure count.
        new ResiliencePipelineBuilder().AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            Name = null,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 8,
            BreakDuration = TimeSpan.FromSeconds(30),
            BreakDurationGenerator = static args =>
                ValueTask.FromResult(TimeSpan.FromSeconds(Math.Min(20 + Math.Pow(2, args.FailureCount), 400))),
            ShouldHandle = new PredicateBuilder().Handle<SomeExceptionType>(),
            OnClosed = null,
            OnOpened = null,
            OnHalfOpened = null,
            ManualControl = null,
            StateProvider = null,
        });

        // Handle specific failed results for HttpResponseMessage:
        var optionsShouldHandle = new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<SomeExceptionType>()
                .HandleResult(response => response.StatusCode == HttpStatusCode.InternalServerError)
        };

        // Monitor the circuit state, useful for health reporting:
        var stateProvider = new CircuitBreakerStateProvider();
        var optionsStateProvider = new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            StateProvider = stateProvider
        };

        var circuitState = stateProvider.CircuitState;

        /*
        CircuitState.Closed - Normal operation; actions are executed.
        CircuitState.Open - Circuit is open; actions are blocked.
        CircuitState.HalfOpen - Recovery state after break duration expires; actions are permitted.
        CircuitState.Isolated - Circuit is manually held open; actions are blocked.
        */

        // Manually control the Circuit Breaker state:
        var manualControl = new CircuitBreakerManualControl();
        var optionsManualControl = new CircuitBreakerStrategyOptions
        {
            ManualControl = manualControl
        };

        // Manually isolate a circuit, e.g., to isolate a downstream service.
        await manualControl.IsolateAsync();

        // Manually close the circuit to allow actions to be executed again.
        await manualControl.CloseAsync();

        // Add a circuit breaker strategy with a CircuitBreakerStrategyOptions{<TResult>} instance to the pipeline
        new ResiliencePipelineBuilder().AddCircuitBreaker(optionsDefaults);
        new ResiliencePipelineBuilder<HttpResponseMessage>().AddCircuitBreaker(optionsStateProvider);

        #endregion
    }

    public static void AntiPattern_CircuitAwareRetry()
    {
        #region circuit-breaker-anti-pattern-circuit-aware-retry
        var stateProvider = new CircuitBreakerStateProvider();
        var circuitBreaker = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new()
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
                BreakDuration = TimeSpan.FromSeconds(5),
                StateProvider = stateProvider
            })
            .Build();

        var retry = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<BrokenCircuitException>(),
                DelayGenerator = args =>
                {
                    TimeSpan? delay = TimeSpan.FromSeconds(1);
                    if (stateProvider.CircuitState == CircuitState.Open)
                    {
                        delay = TimeSpan.FromSeconds(5);
                    }

                    return ValueTask.FromResult(delay);
                }
            })
            .Build();

        #endregion
    }

    private static readonly ResiliencePropertyKey<TimeSpan?> SleepDurationKey = new("sleep_duration");
    public static void Pattern_CircuitAwareRetry()
    {
        #region circuit-breaker-pattern-circuit-aware-retry
        var circuitBreaker = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new()
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
                BreakDuration = TimeSpan.FromSeconds(5),
                OnOpened = static args =>
                {
                    args.Context.Properties.Set(SleepDurationKey, args.BreakDuration);
                    return ValueTask.CompletedTask;
                },
                OnClosed = args =>
                {
                    args.Context.Properties.Set(SleepDurationKey, null);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        var retry = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<BrokenCircuitException>(),
                DelayGenerator = static args =>
                {
                    _ = args.Context.Properties.TryGetValue(SleepDurationKey, out var delay);
                    delay ??= TimeSpan.FromSeconds(1);
                    return ValueTask.FromResult(delay);
                }
            })
            .Build();

        #endregion
    

    public static async ValueTask Pattern_ReduceThrownExceptions()
    {
        #region circuit-breaker-pattern-reduce-thrown-exceptions

        var context = ResilienceContextPool.Shared.Get();
        var circuitBreaker = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new()
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>(),
                BreakDuration = TimeSpan.FromSeconds(0.5),
            })
            .Build();

        Outcome<HttpResponseMessage> outcome = await circuitBreaker.ExecuteOutcomeAsync(static async (ctx, state) =>
        {
            var response = await IssueRequest();
            return Outcome.FromResult(response);
        }, context, "state");

        ResilienceContextPool.Shared.Return(context);

        if (outcome.Exception is BrokenCircuitException)
        {
            // The execution was stopped by the circuit breaker
        }
        else
        {
            HttpResponseMessage response = outcome.Result!;
            // Your code goes here to process the response
        }

        #endregion
    }
}
