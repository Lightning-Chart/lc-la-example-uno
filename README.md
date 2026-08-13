# LightningChart for Uno Platform

This Uno Platform 6.6.29 example opens in historical mode with 1,000,000 samples. Use the real-time control to start or stop 10,000-sample batches.

Learn more: [LightningChart documentation](https://lightningchart.com/lc-la/docs/)

## Run

1. Install the .NET 10 SDK. The Windows target requires Windows 10 build 19041 or later and the Microsoft Edge WebView2 Runtime (normally already installed).
2. Get a [free LightningChart JS trial key](https://lightningchart.com/js-charts/docs/licenses/trials/) and set it in PowerShell:

   ```powershell
   $env:LCJS_LICENSE_KEY="your-license-key"
   ```

3. Run the project:

   ```powershell
   dotnet run --project .\LightningChartUnoExample.csproj -p:LclaUseLocalSource=true
   ```

The app opens with historical data. Select **Start real-time** to stream, and **Stop real-time** to pause it.
