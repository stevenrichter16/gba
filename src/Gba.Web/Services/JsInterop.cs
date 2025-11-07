using Microsoft.JSInterop;

namespace Gba.Web.Services;

public sealed class JsInterop
{
    private readonly IJSRuntime _js;

    public JsInterop(IJSRuntime js) => _js = js;

    public ValueTask InitCanvasAsync(string id, int width, int height, int scale)
        => _js.InvokeVoidAsync("gba.initCanvas", id, width, height, scale);

    public ValueTask PresentAsync(uint[] rgba)
        => _js.InvokeVoidAsync("gba.presentFrame", rgba);

    public ValueTask StartRafAsync<T>(DotNetObjectReference<T> dotNetRef) where T : class
        => _js.InvokeVoidAsync("gba.startRaf", dotNetRef);
}
