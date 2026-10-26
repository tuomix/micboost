## Install

1. Install **VB-CABLE** (free) from <https://vb-audio.com/Cable/>: unzip, right-click `VBCABLE_Setup_x64.exe` → *Run as administrator* → *Install Driver*, then restart Windows.
2. Download and run **MicBoost-Setup.exe** below (no administrator rights needed).
3. In MicBoost, check that **Microphone (HyperX Cloud III)** is the input, and set the boost.
4. In Discord, games and Windows, choose **CABLE Output (VB-Audio Virtual Cable)** as the microphone.

No installer wanted? Use **MicBoost-portable.zip**: unzip and run `MicBoost.exe`.

**Windows SmartScreen** may say "Windows protected your PC" because MicBoost is not code-signed. Click **More info → Run anyway**.
To make sure the file is genuine, compare its checksum with `SHA256SUMS.txt`:

```powershell
Get-FileHash .\MicBoost-Setup.exe -Algorithm SHA256
```

Full instructions, troubleshooting and uninstall steps: see the [README](https://github.com/tuomix/micboost#readme).
