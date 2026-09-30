# RDock

Windows 版類 macOS / RocketDock 風格的 Dock。  
以 .NET 8 WPF 實作：透明置頂、魚眼縮放、自動隱藏、多螢幕與高 DPI。

![RDock Icon](Assets/RDock-Icon.png)

## 功能

- 魚眼放大、拖放新增捷徑／應用程式
- 自動隱藏與螢幕邊緣喚醒
- 圖示磁碟快取（`%AppData%/RDock/Cache`）加速啟動
- 多螢幕（單一螢幕或所有螢幕）、上下左右停靠
- 右鍵選單：鎖定、開機啟動、自訂圖示、資源回收筒、資料夾堆疊
- 不搶焦點（`WS_EX_NOACTIVATE`）、全螢幕時避讓

## 需求

- Windows 10 / 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（自行編譯時）

## 建置與執行

```bash
dotnet build -c Release
dotnet run -c Release --project RDock.csproj
```

輸出位於：

```text
bin/Release/net8.0-windows/RDock.exe
```

## 設定與快取位置

| 項目 | 路徑 |
|------|------|
| Dock 設定 | `%AppData%/RDock/dock_config.json`（會自動從舊版 `MyDock` 遷移） |
| 圖示快取 | `%AppData%/RDock/Cache/` |
| 自訂圖示 | `%AppData%/RDock/Custom/` |

## 版本

目前版本請見 `RDock.csproj` 的 `<Version>`，或於 Dock 右鍵選單／「關於我」查看。

## 授權

以 [MIT License](LICENSE) 釋出。歡迎 Issue / Pull Request。
