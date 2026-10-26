---
name: hello-skill
description: 示例 skill：演示最简单的单文件写法，可随时删掉。
---

# Hello skill

这是一个示例 skill，用来演示单文件形态（`hello-skill.md`）长什么样。

- 第一行到第二行 `---` 之间是 frontmatter，`name` 和 `description` 必须有。
- `description` 会被 DSH 用来判断「什么时候该自动调用这个 skill」，写清楚触发场景。
- 下面的正文就是模型真正读到的说明。

想试试开关效果，就在管理器里把它切成「已禁用」或「已卸载」；
想彻底清干净，选中它点「彻底删除」——或者直接把 `examples\skills` 整个目录删掉。
