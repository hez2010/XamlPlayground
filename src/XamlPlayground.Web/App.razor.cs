using Avalonia;
using Avalonia.Browser.Blazor;

namespace XamlPlayground.Web;

public partial class App
{
    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        WebAppBuilder.Configure<XamlPlayground.App>()
            .SetupWithSingleViewLifetime();
    }
}
