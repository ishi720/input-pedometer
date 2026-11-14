# input-pedometer
キーボードやマウス操作の万歩計

## バッジ

[![Codacy Badge](https://app.codacy.com/project/badge/Grade/6a398fbd57df45d69a2a00c34817f21a)](https://app.codacy.com/gh/ishi720/input-pedometer/dashboard?utm_source=gh&utm_medium=referral&utm_content=&utm_campaign=Badge_grade)

## 動作環境
 
- Windows 10（1703以降）/ Windows 11
- .NET Framework 4.x

## 画面イメージ

<img width="360" height="253" alt="スクリーンショット 2026-11-14 130223" src="https://github.com/user-attachments/assets/ead92ef4-c838-482f-a1be-b20f7ee82991" />

<img width="637" height="360" alt="スクリーンショット 2026-11-14 125650" src="https://github.com/user-attachments/assets/45523beb-8306-481f-904f-865824f2a71c" />

## ビルド
 
`build.bat` を実行

```
build.bat
```

または、Windowsに標準で入っているコンパイラで直接ビルド
 
```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /out:InputPedometer.exe InputPedometer.cs
```

## 自動起動

画面の「Windows起動時に自動で起動」にチェックを入れると、PC起動時に最小化した状態で起動。
（`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` に登録。管理者権限は不要）

exe を移動した場合は、移動後に一度起動すれば登録先のパスも更新される。

