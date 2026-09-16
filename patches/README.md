# 加长图配置3倍数格式 - 维护说明

## 当前状态

- **功能已实现**：配置3 现在输出 `A1x1.25`、`A2x1.5` 形式
- **代码位置**：分支 `feature/config3-multiplier`，已提交
- **补丁文件**：`patches/config3-multiplier.patch`

修改只涉及 3 个文件、34 行代码：

| 文件 | 改动 |
| --- | --- |
| `src/Common/Models/AppSettingsStore.cs` | 枚举 `Reserved2` 改名 `Multiplier` |
| `src/Common/Utilities/FileNameSanitizer.cs` | 新增倍数转换逻辑 |
| `src/Common/Views/SettingsForm.xaml.cs` | 设置界面下拉项文案 |

## 原作者更新后怎么操作

### 推荐做法：合patch（一条命令）

```bash
cd "M:\软件\Autocad\LA批打印-AutoCAD2015-2024\batchPrintZWCAD"

# 1. 切回主干，拉取原作者最新代码
git checkout main
git pull origin main

# 2. 把功能分支变基到最新主干上
git checkout feature/config3-multiplier
git rebase main

# 3. 重新编译
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

`FileNameSanitizer.NormalizeLongPaperFraction` 方法开头需要有这样一段：

```csharp
if (format == LongPaperNameFormat.Multiplier)
{
    // 分数形式：A1+1/4 -> A1x1.25
    var result = LongPaperFractionPattern.Replace(paperName, match =>
    {
        var numerator = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var denominator = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (denominator == 0) return match.Value;
        var ext = numerator / (double)denominator;
        return "x" + (1.0 + ext).ToString("0.##", CultureInfo.InvariantCulture);
    });

    // 小数形式：A1+0.25 -> A1x1.25
    result = LongPaperDecimalExtPattern.Replace(result, match =>
    {
        var dec = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (dec <= 0d) return match.Value;
        return "x" + (1.0 + dec).ToString("0.##", CultureInfo.InvariantCulture);
    });

    return result;
}
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
