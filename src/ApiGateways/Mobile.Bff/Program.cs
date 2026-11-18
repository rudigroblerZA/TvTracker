using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver()
    .AddTransforms(context =>
    {
        // Behind the front door (Caddy), X-Forwarded-For already names the visitor; appending Caddy's own address as
        // the gateway would makes every anonymous caller share one rate-limit key (ClientRateLimiting.PartitionKey
        // uses the last entry). Reached directly (development), the header is added as usual.
        context.AddXForwarded(ForwardedTransformActions.Off);
        context.AddRequestTransform(transform =>
        {
            var http = transform.HttpContext;
            if (!http.Request.Headers.ContainsKey("X-Forwarded-For") && http.Connection.RemoteIpAddress is { } peer)
            {
                transform.ProxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", peer.ToString());
            }
            return ValueTask.CompletedTask;
        });
    });

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapReverseProxy();

app.Run();
