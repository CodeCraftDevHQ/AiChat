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

    void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
    }
}
