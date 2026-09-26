# 加长图配置3倍数格式 - 维护说明

## 当前状态

- **功能已实现**：配置3 现在输出 `A1x1.25`、`A2x1.5` 形式
- **代码位置**：分支 `feature/config3-multiplier`，已提交
- **补丁文件**：`patches/config3-multiplier.patch`
- **PR 文案**：`patches/PR_DESCRIPTION.md`

修改只涉及 3 个文件、42 行新增 / 6 行修改：

| 文件 | 改动 |
| --- | --- |
| `src/Common/Models/AppSettingsStore.cs` | 枚举 `Reserved2` 改名 `Multiplier`，占位值仍为 `2` |
| `src/Common/Utilities/FileNameSanitizer.cs` | 新增倍数分支，抽出 `FormatMultiplier`，新增 `LongPaperNumberExtPattern` 正则 |
| `src/Common/Views/SettingsForm.xaml.cs` | 设置界面下拉项文案 |

## 原作者更新后怎么操作

### 推荐做法：变基（rebase）

```bash
cd "M:\软件\Autocad\LA批打印-AutoCAD2015-2024\batchPrintZWCAD"

# 1. 切回主干，拉取原作者最新代码
git checkout main
git pull origin main

# 2. 把功能分支变基到最新主干上
git checkout feature/config3-multiplier
git rebase main

# 3. 重新编译（先关闭 CAD，否则 DLL 被锁定会编译失败）
powershell -ExecutionPolicy Bypass -File scripts\build-dll.ps1 -Target All -Configuration Release
```

如果 rebase 报告冲突，通常只会出现在 `FileNameSanitizer.cs`，因为那是改动最大的文件。打开该文件，搜索 `Multiplier`，参考下面的"转换逻辑参考"手动补齐即可，然后：

```bash
git add src/Common/Utilities/FileNameSanitizer.cs
git rebase --continue
```

### 备用做法：补丁重新应用

如果分支被弄乱了，用补丁最干净：

```bash
cd "M:\软件\Autocad\LA批打印-AutoCAD2015-2024\batchPrintZWCAD"

git checkout main
git pull origin main
git checkout -B feature/config3-multiplier
git am patches/config3-multiplier.patch

powershell -ExecutionPolicy Bypass -File scripts\build-dll.ps1 -Target All -Configuration Release
```

## 转换逻辑参考

`FileNameSanitizer.NormalizeLongPaperFraction` 方法开头需要有这样一段（与当前分支实际代码一致）：

```csharp
// ── 配置3（倍数）：将加长图转换为"图幅x放大倍数"形式 ──
if (format == LongPaperNameFormat.Multiplier)
{
    // 先处理已有 "/" 的分数形式（如 A1+1/2）
    var multiplierResult = LongPaperFractionPattern.Replace(paperName, match =>
    {
        var numerator = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var denominator = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (denominator == 0) return match.Value;
        var ext = numerator / (double)denominator;
        return FormatMultiplier(ext);
    });

    // 再处理整数或小数扩展量（如 A1+1、A1+0.25）
    multiplierResult = LongPaperNumberExtPattern.Replace(multiplierResult, match =>
    {
        var ext = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return ext <= 0d ? match.Value : FormatMultiplier(ext);
    });

    return multiplierResult;
}
```

辅助方法与正则：

```csharp
/// <summary>把加长扩展量换算为"图幅x总倍数"形式，如 0.25 → x1.25、1 → x2。</summary>
private static string FormatMultiplier(double extension)
{
    // 最多3位小数，覆盖 1/8 模数（0.125）而不产生多余尾零。
    return "x" + (1.0 + extension).ToString("0.###", CultureInfo.InvariantCulture);
}

// 匹配末尾整数或小数扩展量，如 +1、+0.5、+1.125
private static readonly Regex LongPaperNumberExtPattern =
    new Regex(@"\+(\d+(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
```

枚举定义：

```csharp
public enum LongPaperNameFormat
{
    Fraction = 0,    // 配置1：A3+1/8
    Decimal = 1,     // 配置2：A3+0.125
    Multiplier = 2,  // 配置3：A1x1.25  ← 本次新增
    Reserved3 = 3,
    Reserved4 = 4,
    Reserved5 = 5,
}
```

设置界面文案：

```csharp
_longPaperNameFormat.Items.Add("配置3（倍数）：A1x1.25、A2x1.5（倍数形式）");
```

### 转换行为对照

| 输入 | 配置3 输出 | 说明 |
| --- | --- | --- |
| `A1+1/4` | `A1x1.25` | 分数输入 |
| `A1+1/8` | `A1x1.125` | 1/8 模数，3 位小数 |
| `A1+0.25` | `A1x1.25` | 小数输入 |
| `A1+1` | `A1x2` | 整数加长 |
| `A0`、`A1` 等标准图幅 | 原样返回 | 无 `+` 后缀不处理 |
| `A1+1.501` | `A1x2.501` | 任意加长 |

## 关于上游更新

原作者仓库如果改了这块逻辑，最可能需要重新确认的点：

1. `OutputPaperNameResolver.Resolve` 返回值是否仍是 `A1+0.25` 这种"加号 + 小数"形式。如果是，倍数逻辑不用改。
2. `LongPaperNameFormat` 枚举是否新增或重排了值。枚举值 `2` 必须留给 `Multiplier`，否则用户已保存的配置会错位。
3. `NormalizeLongPaperFraction` 的方法签名是否变化。

## 日常检查

```bash
# 看上游有没有更新
git fetch origin
git log --oneline main..origin/main

# 确认自己的功能还在
git log --oneline --all | grep config3
```
