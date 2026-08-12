# LightningChart for Uno Platform

This Uno Platform example loads 1,000,000 historical samples and then streams 10,000-sample batches.

## Run

1. Install the .NET 8 SDK and the Uno Platform tooling.
2. Set a LightningChart JS license key:

   ```powershell
   $env:LCJS_LICENSE_KEY="your-license-key"
   ```

3. Run the Windows target:

   ```bash
   dotnet run -f net8.0-windows10.0.19041
   ```

Use the Uno tooling to select another supported target.
