using FyersLoginWeb.Models;
using FyersLoginWeb.Services;
using FyersLoginWeb.Services.Broker;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Fyers config bind karo aur auth service register karo (HttpClient ke saath)
builder.Services.Configure<FyersSettings>(builder.Configuration.GetSection("Fyers"));
builder.Services.AddHttpClient<FyersAuthService>();
builder.Services.AddHttpClient<FyersHistoryService>();
builder.Services.AddSingleton<DbService>();

// Paper broker + position manager — SINGLETON (LiveTradingService aur Dashboard dono share karein)
builder.Services.AddSingleton<PaperBrokerOrders>(sp =>
    new PaperBrokerOrders(sp.GetRequiredService<ILogger<PaperBrokerOrders>>()));
builder.Services.AddSingleton<IBrokerOrders>(sp => sp.GetRequiredService<PaperBrokerOrders>());
builder.Services.AddSingleton<PositionManager>(sp =>
    new PositionManager(sp.GetRequiredService<IBrokerOrders>(),
        sp.GetRequiredService<ILogger<PositionManager>>(), new TimeSpan(14, 45, 0)));

builder.Services.AddHostedService<LiveTradingService>();

var app = builder.Build();

// DB + tables ensure
using (var scope = app.Services.CreateScope())
{
    try { scope.ServiceProvider.GetRequiredService<DbService>().EnsureCreated(); }
    catch (Exception ex) { Console.WriteLine("DB init error: " + ex.Message); }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
