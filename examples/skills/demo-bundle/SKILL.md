---
name: demo-bundle
description: 示例 skill：演示目录式写法（一个目录 + SKILL.md），可以在同目录放参考资料和脚本。
---

# Demo bundle

这个 skill 用的是目录式形态：

```
demo-bundle\
  SKILL.md        <- 必须有，文件名固定
  reference.md    <- 可选：参考文档
  scripts\        <- 可选：脚本
```

开关、卸载、删除的管理方式跟单文件形态完全一样：管理器操作的是整个目录。

本目录里的 `reference.md` 是随包附带的参考资料，模型需要时会自己去读。
