# 🎮 PS3 Discord Rich Presence

Display your current PlayStation 3 activity on Discord using Rich Presence.

This application connects to your PS3 through **webMAN MOD** and automatically updates your Discord status with the game you're currently playing.

---

## ✨ Features

* Automatically detects the currently running game
* Displays the game's cover art on Discord
* Shows CPU and RSX temperatures
* Detects when the console is on the XMB
* Supports PS3, PS2, PS1, and PSP games
* Automatically updates when you change games
* Lightweight and easy to configure

---

## 📋 Requirements

Before using the application, make sure you have:

* Windows 10 or later
* Discord Desktop installed and running
* A PlayStation 3 with **webMAN MOD** installed
* Your PC and PS3 connected to the same local network

---

## 📥 Installation

1. Download the latest release from the **Releases** page.
2. Run **PS3DiscordRichPresence.exe**.

No installation is required.

---

## ⚙️ Configuration

### 1. Find your PS3 IP address

On your PS3, go to:

**Settings → Network Settings → Connection Status List**

Example:

```text
192.168.1.35
```

---

### 2. Configure the application

Open the configuration file:

```text
PS3config.json
```

Replace the IP address with your PS3's address.

Example:

```json
{
    "Ip": "192.168.1.35"
}
```

Save the file.

---

## 🚀 Usage

1. Open Discord.
2. Turn on your PlayStation 3.
3. Launch **PS3DiscordRichPresence.exe**.
4. Start any supported game.

The application will automatically detect the game and update your Discord Rich Presence.

No additional interaction is required.

---

## 🖼 Example

Discord Rich Presence will display information similar to:

```text
Playing
God of War III

🔥 CPU: 63°C
🔥 RSX: 61°C
```

---

## ❓ Troubleshooting

### The application cannot find my PS3

* Verify that your PC and PS3 are connected to the same network.
* Make sure the IP address in `PS3config.json` is correct.
* Ensure that webMAN MOD is installed and running.
* Verify if **PS3config.json** and **PS3DiscordRichPresence.exe** are in the same directory/folder.

---

### Discord Rich Presence is not updating

* Make sure Discord is open.
* Restart Discord.
* Restart the application.
* Wait a few seconds for the next automatic update.

---

### The game appears as "Unknown"

Some games may not yet be included in the database.

If this happens, please open an Issue and include the game's Title ID.

---

## ❔ Frequently Asked Questions

### Does it work with HEN?

Yes.

### Does it work with Custom Firmware (CFW)?

Yes.

### Does it support RPCS3?

No. This application is designed for real PlayStation 3 consoles.

---

## 🤝 Contributing

Contributions are welcome!

If you'd like to improve the project, fix bugs, or add new features, feel free to submit a Pull Request.

If you encounter any issues, please open an Issue describing the problem.

---

## 📄 License

This project is licensed under the MIT License.
