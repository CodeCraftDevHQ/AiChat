using AiChat.Services;
using Microsoft.AspNetCore.Components;

namespace AiChat.Components;

/// <summary>Base class: injects ChatState and re-renders whenever it changes.</summary>
public abstract class StateComponent : ComponentBase, IDisposable
{
    [Inject] protected ChatState State { get; set; } = default!;

    protected override void OnInitialized()
    {
        State.Changed += OnStateChanged;
    }

    void OnStateChanged()
    {
        // the user is scrolling inside a thinking / tools box: keep the DOM still, render once when released
        if (State.HoldRender) return;
        _ = InvokeAsync(StateHasChanged);
    }

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
    }
}
