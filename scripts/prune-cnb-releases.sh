#!/usr/bin/env bash
# 清理 CNB 版本库，只留大版本。
#
# 为什么要有这东西：CNB 的仓库容量是有限的（免费额度按仓库算），而 CI 每次构建
# 都会往版本库写东西。只要哪天有人把 CNB_TAG 改成带上构建号（build-123、时间戳），
# 版本就会一路堆下去，每个都挂着一个 70 MB 的 zip，几个月就把额度吃干净。
#
# 保留规则（三条，按顺序判断）：
#   1. latest-build          对外下载用的固定入口，永远只覆盖同一个附件，不会累积
#   2. v<主>.<次>[.<修>]     大版本。保留最新的 KEEP 个，更早的删
#   3. 其余                   一律删（build-*、ci-*、时间戳、临时探针）
#
# 用法：
#   CNB_TOKEN=xxx scripts/prune-cnb-releases.sh
#   CNB_TOKEN=xxx DRY_RUN=1 scripts/prune-cnb-releases.sh   # 只看会删什么，不动手
#
# 可选环境变量：
#   CNB_REPO             必填，形如 <用户名>/<仓库名>
#   CNB_API              默认 https://api.cnb.cool
#   CNB_KEEP_MAJOR       保留几个大版本，默认 3
#   CNB_KEEP_LATEST_BUILD 设为 0 时连 latest-build 一起删（只在要彻底清空时用）
#
# 依赖：curl、python3
set -euo pipefail

: "${CNB_TOKEN:?需要 CNB_TOKEN（CNB 访问令牌，需 repo-release:rw 权限）}"

# 中文版本名在 Windows 的 cp1252 stdout 上会抛 UnicodeEncodeError，
# 而 GitHub 的 windows runner 正好是这个情况。只影响打印，但足以把整步弄红。
export PYTHONIOENCODING=utf-8

REPO="${CNB_REPO:-}"
if [ -z "$REPO" ]; then
  echo "需要 CNB_REPO（形如 alice/classpoints）" >&2
  exit 2
fi
API="${CNB_API:-https://api.cnb.cool}"
KEEP_MAJOR="${CNB_KEEP_MAJOR:-3}"
KEEP_LATEST="${CNB_KEEP_LATEST_BUILD:-1}"
DRY="${DRY_RUN:-0}"

api() {
  local method="$1" path="$2"
  curl -sS -X "$method" "$API$path" \
    -H "Authorization: Bearer $CNB_TOKEN" \
    -H "Accept: application/json" \
    -w '\n%{http_code}'
}

# ---------- 1) 把全部 release 拉下来（分页） ----------
# 用内存里的 NDJSON 攒，不落临时文件：Windows 上 mktemp 返回的是 C:\…\Temp/xxx 这种
# 反斜杠 + 盘符混排的路径，环境里的删除保护会把它当成非法路径拒掉，脚本最后那步
# trap 清理就会把退出码改成 1，看起来像"清理失败"，其实全跑完了。
echo "==> 拉取 $REPO 的版本列表"
page=1
ndjson=""
while :; do
  resp=$(api GET "/$REPO/-/releases?page=$page&page_size=100")
  code=$(printf '%s' "$resp" | tail -n1)
  body=$(printf '%s' "$resp" | sed '$d')
  if [ "$code" != "200" ]; then
    echo "拉取失败（HTTP $code）：$body" >&2
    exit 1
  fi

  chunk=$(printf '%s' "$body" | python3 -c '
import json,sys
d=json.load(sys.stdin)
items=d if isinstance(d,list) else (d.get("list") or d.get("releases") or d.get("data") or [])
for r in items:
    assets=r.get("assets") or []
    size=sum((a.get("size") or 0) for a in assets)
    print(json.dumps({"id":str(r.get("id")),"tag":r.get("tag_name") or "","name":r.get("name") or "","size":size,"created":r.get("created_at") or ""},ensure_ascii=False))
print("###COUNT\t%d" % len(items))
')
  count=$(printf '%s\n' "$chunk" | grep '^###COUNT' | cut -f2)
  ndjson+=$(printf '%s\n' "$chunk" | grep -v '^###COUNT')$'\n'

  if [ "${count:-0}" = "0" ]; then break; fi

  # 不足一页说明到底了。用 page_size 判断而不是"有没有下一页"，
  # 因为 CNB 在不同版本上对 last_page / total 字段的返回不一致。
  if [ "$count" -lt 100 ]; then break; fi
  page=$((page + 1))
done

total=$(printf '%s' "$ndjson" | grep -c . || true)
echo "    共 $total 个版本"

# ---------- 2) 算出该删哪些 ----------
# 排序和分组放在 python 里做：版本号比较用字符串排会得到 v1.10 < v1.9，
# 而"删最旧的大版本"这件事一旦排错，删掉的就是不该删的那个。
# 注意：这里必须先把 python 代码放进变量，再 `python3 -c "$code"`。
# 写成 `printf ... | python3 - <<'PY'` 的话，here-doc 会把 stdin 抢走，
# python 读到的是脚本自己那段源码，不是管道里的 JSON —— 结果是静默地"什么都不用删"。
read -r -d '' PLAN_PY <<'PY' || true
import json, re, sys

keep_major, keep_latest = int(sys.argv[1]), sys.argv[2] == "1"
items = [json.loads(l) for l in sys.stdin if l.strip()]

major_re = re.compile(r"^v(\d+)\.(\d+)(?:\.(\d+))?$")
majors, others = [], []
for r in items:
    m = major_re.match(r["tag"])
    if m:
        key = tuple(int(x) if x else 0 for x in m.groups())
        majors.append((key, r))
    else:
        others.append(r)

# 大版本只留最新的 N 个；同版本号重复时（理论上不该有）只留 id 最大的那个
majors.sort(key=lambda p: (p[0], p[1]["id"]), reverse=True)
drop = [r for _, r in majors[keep_major:]]

for r in others:
    if r["tag"] == "latest-build" and keep_latest:
        continue
    drop.append(r)
    # tag == latest-build 且 keep_latest 为 0 → 落进删除

for r in sorted(drop, key=lambda x: x["created"]):
    print("%s\t%s\t%s\t%d" % (r["id"], r["tag"], r["name"], r["size"]))
PY

plan=$(printf '%s' "$ndjson" | python3 -c "$PLAN_PY" "$KEEP_MAJOR" "$KEEP_LATEST")

targets="$plan"

if [ -z "$targets" ]; then
  echo "==> 没有需要清理的版本（保留 $total 个）"
  exit 0
fi

# ---------- 3) 逐个删 ----------
del_count=0
del_bytes=0
while IFS=$'\t' read -r id tag name size; do
  if [ -z "$id" ]; then continue; fi
  mb=$(( size / 1048576 ))
  if [ "$DRY" = "1" ]; then
    echo "    [dry-run] 会删 $tag（$name，${mb} MB）"
  else
    code=$(curl -sS -X DELETE "$API/$REPO/-/releases/$id" \
      -H "Authorization: Bearer $CNB_TOKEN" \
      -H "Accept: application/json" \
      -o /dev/null -w '%{http_code}')
    # 204 = 删掉了；404 = 别人已经删了（并发跑两个 CI 时会这样），都算成功
    if [ "$code" = "204" ] || [ "$code" = "404" ]; then
      echo "    删除 $tag（$name，${mb} MB）→ $code"
    else
      echo "    删除 $tag 失败（HTTP $code），继续处理下一个" >&2
      continue
    fi
  fi
  del_count=$((del_count + 1))
  del_bytes=$((del_bytes + size))
done <<< "$targets"

echo
if [ "$DRY" = "1" ]; then
  echo "==> 预演：将删除 $del_count 个版本，回收约 $((del_bytes / 1048576)) MB"
  echo "    确认无误后去掉 DRY_RUN=1 再跑一次"
else
  echo "==> 已删除 $del_count 个版本，回收约 $((del_bytes / 1048576)) MB；保留 $((total - del_count)) 个"
fi
echo "    版本页：https://cnb.cool/$REPO/-/releases"
