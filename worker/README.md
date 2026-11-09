# 同步中枢 · Cloudflare Worker + D1

这一层只做四件事：**鉴权、校验、幂等写入、增量查询**。不排名、不折算、不做任何业务计算 —— 计算全留在客户端，
所以换算法不用动服务端，服务端挂了也不影响教室里加分。

## 一、它长什么样

```
客户端 A ──┐                        ┌── D1: classes / members / devices
           ├─ HTTPS + Bearer ── Worker ── students / entries / periods
客户端 B ──┘                        └── exams / exam_scores / rules / schemes
                                        + changelog（增量游标）
```

- **鉴权**：`POST /classes` 建班返回 access（12 小时）+ refresh（90 天）；`POST /pair` 用班级码 + 密码配对第二台设备。
  令牌是 HMAC-SHA256 签的紧凑 JWT，密码用 PBKDF2（Workers 免费版上限 10 万轮，正好够用）。
- **幂等**：所有写操作以客户端生成的 UUIDv7 为主键，重复提交只更新不重复插入。
- **并发**：每张表带 `rev`，客户端带着读到的 `rev` 写，服务端 `rev = rev + 1` 后比对，落后就 409。
- **增量**：任何写入都往 `changelog` 追加一条，`GET /sync?since=<seq>` 按游标返回，客户端记 `next`。
- **周期重置**：`POST /periods/reset` 两阶段（prepare → commit），重置期间普通写入返回 423，
  免得一台设备的写入落到已经归档的周期里。

## 二、本地跑

```bash
cd worker
npm install
npx wrangler d1 migrations apply classpoints --local
npx wrangler dev --port 8787 --local
```

`.dev.vars` 里已经放了本地用的 `TOKEN_SECRET`，只对 `--local` 生效，不要拿去部署。

跑契约测试（58 项，覆盖建班、配对、幂等重传、并发冲突、周期重置、撤销、越权、**多班隔离**）：

```bash
npm run typecheck
npm run test:smoke
```

本地 D1 是持久化的，反复跑会累积数据。想从干净库开始：

```bash
npm run db:reset      # 把 .wrangler/state 挪走并重新应用迁移
```

## 三、部署到 Cloudflare

```bash
# 1. 登录
npx wrangler login

# 2. 建 D1 库，把输出的 database_id 填进 wrangler.toml
npx wrangler d1 create classpoints

# 3. 建表（远端）
npx wrangler d1 migrations apply classpoints --remote

# 4. 设令牌签名密钥（32 字节以上随机串，自己生成一个存好）
npx wrangler secret put TOKEN_SECRET

# 5. 发布
npx wrangler deploy
```

发布后会得到一个 `https://classpoints-api.<你的账号>.workers.dev` 地址，填到电脑端
`设置 → 同步 → 服务器地址` 即可。第一台设备点「在这台电脑上建班」，拿到 8 位班级码给其它设备配对。

### 一定要绑自有域名

`*.workers.dev` 在国内网络下经常直接不通（代理 `CONNECT` 返回 502），表现成「同步一直失败」，
而开发机上因为有代理完全看不出来。在 `wrangler.toml` 里挂一条路由就行：

```toml
[[routes]]
pattern = "classpoints.你的域名.com"
custom_domain = true
```

前提是这个域名的 DNS 已经托管在同一家 Cloudflare 账号下 —— 不然 wrangler 找不到 zone，
`deploy` 会失败。`custom_domain = true` 会自动建 DNS 记录、自动签证书，不用手动去 DNS 面板加。

本项目部署时用的就是自有域名。`classpoints.你的域名.com` 这种写法要自己替换 ——
**换域名时保留旧路由一段时间**，比让已经装好的机器手动改设置稳妥：
旧域名删掉的那一刻，所有填了它的机器会同时同步不了，而用户不会知道该去哪里改。

### 免费额度够不够

一个 45 人的班，一学期大约 3000 条流水，边写边同步：

| 资源 | 免费额度 | 实际用量 |
| --- | --- | --- |
| Workers 请求 | 10 万次/天 | 每天几百次（写操作合并成批，读按 30 秒节流） |
| D1 存储 | 5 GB | 一学期不到 20 MB |
| D1 读行 | 500 万行/天 | 增量拉取，每次只读变更 |

一个账号可以放几十个班，不必按班建库。**每张业务表都带 `class_id`**，令牌里带 `cid`，
所有读写都按 `claims.cid` 收口：

- `/sync` 只读 `WHERE class_id = ?` 的 changelog 与业务行；
- 通用实体的写路由把 `class_id` 写死成 `claims.cid`，`ON CONFLICT` 的 `DO UPDATE` 还带
  `AND 表.class_id = excluded.class_id`，跨班撞同一个主键 id 时宁可不落地；
- 唯一约束全是"班内唯一"（学号、常用评语、同期 active 周期、启用中的折算方案），
  两个班各自有 01 号学生、各自有一个当前周期，互不相干。

越权的结果是"看不见"而不是"报错"：拿到 A 班令牌去删 B 班的记录会返回 200 但改动 0 行，
去撤 B 班的批次会 404。冒烟测试第 9 节专门钉这一组行为。

> 历史说明：`0001_init.sql` 在**真实 Cloudflare 部署之前**重写过一次（从"中心化单班"
> 改成"全表带 `class_id`"）。那时远端 D1 还不存在，没有需要兼容的线上数据。
> 一旦上过线，改结构就必须新增 `0002_xxx.sql` 走 SQLite 的 12 步表重建，不能再动这个文件。

## 四、接口一览

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/v1/ping` | 探活；带令牌时验令牌链路（一体机休眠后 TCP 假活，先打这个） |
| POST | `/api/v1/classes` | 建班，返回班级码 + 双令牌（全程只调用一次） |
| POST | `/api/v1/pair` | 班级码 + 密码（或恢复码）配对设备 |
| POST | `/api/v1/refresh` | 换 access，refresh 一次一换 |
| POST | `/api/v1/signout` | 撤销本设备 refresh |
| GET | `/api/v1/sync?since=&limit=` | 增量拉取，返回各实体当前状态 + `deleted` + `next` |
| POST | `/api/v1/entries` | 流水批量入账 1–50 条，可同批带冲销行 |
| POST | `/api/v1/entries/batch/:id/reverse` | 按批次整批撤销 |
| POST | `/api/v1/exams` | 一场考试 + 全部成绩（整批一个事务） |
| POST | `/api/v1/periods` | 周期 upsert |
| POST | `/api/v1/periods/reset` | 两阶段重置（`request` / `renew` / `abort` / `commit`） |
| POST/PATCH | `/api/v1/:entity` | `students` / `rules` / `members` / `note_templates` / `schemes` |
| POST | `/api/v1/schemes/:id/weights` | 换整张考试权重表，不是逐条改 |

所有写接口的请求体形状：`{ action: "upsert" | "delete" | "restore", <实体名>: { ... } }`，
和客户端 `outbox.payload` 一字不差 —— 客户端把 payload 原样转发，不重新组装。

## 五、表结构

`migrations/0001_init.sql` 里 15 张表，关键约束：

- **每张业务表都带 `class_id TEXT NOT NULL`**，所有唯一约束都是班内唯一。
- `students` 上 `UNIQUE(class_id, student_no) WHERE student_no IS NOT NULL` ——
  学号在班里不能重（两个班各自的「01 号」互不影响）。同名同姓靠学号区分，系统不做姓名归一。
- `entries` 主键是服务端 `seq`，客户端 UUID 走 `UNIQUE(class_id, id)` 当幂等键；
  `batch_id` 保证一批加分原子可见；冲销行用 `reverses_entry_id` 指向原行，
  `UNIQUE(class_id, reverses_entry_id) WHERE reverses_entry_id IS NOT NULL` 保证同一批不会被撤两次。
- `periods` 上 `UNIQUE(class_id) WHERE status='active'` —— 一个班永远只有一个当前周期。
- `schemes` 上 `UNIQUE(class_id) WHERE is_active = 1` —— 一个班只有一个启用中的折算方案。
- `changelog(class_id, seq AUTOINCREMENT)` 是增量游标，`(class_id, entity, row_id)` 记录每次变更。
  `seq` 全局自增，但每个班只读 `class_id = ? AND seq > ?`，对它自己来说游标仍然连续单调。

## 六、注意

- 密码只在服务端做 PBKDF2，**明文不落库、不入日志**。
- `TOKEN_SECRET` 换掉之后所有令牌立即失效，客户端会提示重新配对。
- 这一层故意不存「总分」。总分是客户端 `SUM(delta)` 现算的，服务端存分数就等于埋一个必然对不上的冗余字段。
