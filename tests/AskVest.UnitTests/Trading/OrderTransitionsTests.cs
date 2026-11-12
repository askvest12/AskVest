using AskVest.Modules.Trading.Domain;
using Shouldly;
using Xunit;

namespace AskVest.UnitTests.Trading;

public sealed class OrderTransitionsTests
{
    private static readonly (OrderState From, OrderState To)[] LegalPairs =
    [
        (OrderState.PendingSubmission, OrderState.Submitting),
        (OrderState.PendingSubmission, OrderState.Cancelled),
        (OrderState.Submitting, OrderState.Submitted),
        (OrderState.Submitting, OrderState.Failed),
        (OrderState.Submitting, OrderState.NeedsReconciliation),
        (OrderState.Submitted, OrderState.Executed),
        (OrderState.Submitted, OrderState.Failed),
        (OrderState.Submitted, OrderState.NeedsReconciliation),
        (OrderState.NeedsReconciliation, OrderState.Executed),
        (OrderState.NeedsReconciliation, OrderState.Failed),
    ];

    private static readonly HashSet<(OrderState, OrderState)> LegalSet = new(LegalPairs);

    public static TheoryData<OrderState, OrderState> LegalTransitions()
    {
        var data = new TheoryData<OrderState, OrderState>();
        foreach (var (from, to) in LegalPairs)
        {
            data.Add(from, to);
        }

        return data;
    }

    public static TheoryData<OrderState, OrderState> IllegalTransitions()
    {
        var data = new TheoryData<OrderState, OrderState>();
        foreach (var from in Enum.GetValues<OrderState>())
        {
            foreach (var to in Enum.GetValues<OrderState>())
            {
                if (from != to && !LegalSet.Contains((from, to)))
                {
                    data.Add(from, to);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Legal_Transitions_Are_Allowed(OrderState from, OrderState to)
        => OrderTransitions.CanTransition(from, to).ShouldBeTrue($"{from} -> {to}");

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Illegal_Transitions_Are_Refused(OrderState from, OrderState to)
        => OrderTransitions.CanTransition(from, to).ShouldBeFalse($"{from} -> {to}");

    [Fact]
    public void Self_Transitions_Are_Refused()
    {
        foreach (var state in Enum.GetValues<OrderState>())
        {
            OrderTransitions.CanTransition(state, state).ShouldBeFalse($"{state} -> {state}");
        }
    }

    [Fact]
    public void Final_States_Have_No_Outgoing_Transitions()
    {
        foreach (var terminal in new[] { OrderState.Executed, OrderState.Failed, OrderState.Cancelled })
        {
            foreach (var target in Enum.GetValues<OrderState>())
            {
                OrderTransitions.CanTransition(terminal, target).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void Non_Final_And_Rolling_Membership_Is_Correct()
    {
        OrderTransitions.IsFinal(OrderState.Executed).ShouldBeTrue();
        OrderTransitions.IsFinal(OrderState.Failed).ShouldBeTrue();
        OrderTransitions.IsFinal(OrderState.Cancelled).ShouldBeTrue();
        OrderTransitions.IsFinal(OrderState.NeedsReconciliation).ShouldBeFalse();
        OrderTransitions.IsFinal(OrderState.Submitted).ShouldBeFalse();

        OrderTransitions.CountsTowardRollingTotal(OrderState.Failed).ShouldBeFalse();
        OrderTransitions.CountsTowardRollingTotal(OrderState.Cancelled).ShouldBeFalse();
        OrderTransitions.CountsTowardRollingTotal(OrderState.Executed).ShouldBeTrue();
        OrderTransitions.CountsTowardRollingTotal(OrderState.NeedsReconciliation).ShouldBeTrue();
    }
}
