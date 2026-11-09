#!/usr/bin/env bash
# 把发布包挂到 CNB 的「版本（Release）」上。
#
# 为什么要有这东西：GitHub Actions 编出来的产物只能在 GitHub 页面下载，
# 要登录、14 天就过期、国内还经常连不上。CNB 是国内站点（cnb.cool），
# 把同一个包挂到那边的「版本」里，分发链接就固定了、也不受 artifact 保留期限制。
#
# 这一步是可选的：不配 CNB_REPO / CNB_TOKEN 就完全不用它，GitHub 的产物照旧能下。
#
# 用法：
#   CNB_TOKEN=xxx scripts/upload-cnb-release.sh dist/ClassPoints-win-x64.zip [更多文件...]
#
# 可选环境变量：
#   CNB_REPO       必填，形如 <用户名>/<仓库名>（例如 alice/classpoints）
#   CNB_TAG        默认 latest-build（固定 tag，每次覆盖同名附件，下载地址始终不变）
#   CNB_NAME       Release 标题
#   CNB_BODY       Release 描述（纯文本即可）
#   CNB_API        默认 https://api.cnb.cool
#
# 依赖：curl、python3（GitHub/CNB 的 runner 和本机都有）
set -euo pipefail

: "${CNB_TOKEN:?需要 CNB_TOKEN（CNB 访问令牌，需 repo-release:rw 权限）}"

# Windows 上 python 的 stdout 默认跟系统代码页走（英文系统是 cp1252），
# 一旦往 stdio 里写中文就抛 UnicodeEncodeError。版本名和描述里全是中文，
# 而 GitHub 的 windows runner 恰好就是这个情况 —— 上传已经成功了，
# 结果最后一步构造描述时把整步弄红。
export PYTHONIOENCODING=utf-8

REPO="${CNB_REPO:-}"
if [ -z "$REPO" ]; then
  echo "需要 CNB_REPO（形如 alice/classpoints）" >&2
  exit 2
fi
TAG="${CNB_TAG:-latest-build}"
API="${CNB_API:-https://api.cnb.cool}"
NAME="${CNB_NAME:-构建产物}"
BODY="${CNB_BODY:-由 CI 自动发布。}"

if [ "$#" -lt 1 ]; then
  echo "用法: CNB_TOKEN=xxx $0 <文件> [文件...]" >&2
  exit 2
fi

# CNB 的 OpenAPI 认 Accept 头，缺了会回 406 而不是 401，很容易误判成权限问题
api() {
  local method="$1" path="$2" data="${3:-}"
  if [ -n "$data" ]; then
    curl -sS -X "$method" "$API$path" \
      -H "Authorization: Bearer $CNB_TOKEN" \
      -H "Accept: application/json" \
      -H "Content-Type: application/json" \
      -d "$data" -w '\n%{http_code}'
  else
    curl -sS -X "$method" "$API$path" \
      -H "Authorization: Bearer $CNB_TOKEN" \
      -H "Accept: application/json" \
      -w '\n%{http_code}'
  fi
}

# verify_url 返回的是完整地址（自带 https://api.cnb.cool 前缀），不能再拼一次
api_abs() {
  local url="$1"
  curl -sS -X POST "$url" \
    -H "Authorization: Bearer $CNB_TOKEN" \
    -H "Accept: application/json" \
    -H "Content-Type: application/json" \
    -w '\n%{http_code}'
}

# 把响应体与状态码拆开
body_of() { printf '%s' "$1" | sed '$d'; }
code_of() { printf '%s' "$1" | tail -n1; }

json_get() {
  python3 -c 'import json,sys;d=json.load(sys.stdin);print(d.get(sys.argv[1],"") or "")' "$1"
}

# ---------- 1) 找到或创建 Release ----------
echo "==> 查找版本 $TAG"
resp=$(api GET "/$REPO/-/releases/tags/$TAG")
rid=""
if [ "$(code_of "$resp")" = "200" ]; then
  rid=$(body_of "$resp" | json_get id)
  echo "    已存在，id=$rid"
fi

if [ -z "$rid" ]; then
  echo "==> 创建版本 $TAG"
  payload=$(python3 - "$TAG" "$NAME" "$BODY" <<'PY'
import json, sys
tag, name, body = sys.argv[1], sys.argv[2], sys.argv[3]
# ensure_ascii=True：让 JSON 里只出现 \uXXXX 转义，避免 stdout 编码问题
print(json.dumps({
    "tag_name": tag,
    "name": name,
    "body": body,
    "target_commitish": "main",
    "make_latest": "false",
    "prerelease": False,
}, ensure_ascii=True))
PY
)
  resp=$(api POST "/$REPO/-/releases" "$payload")
  code=$(code_of "$resp")
  if [ "$code" != "201" ]; then
    echo "创建版本失败（HTTP $code）：$(body_of "$resp")" >&2
    exit 1
  fi
  rid=$(body_of "$resp" | json_get id)
  echo "    id=$rid"
fi

# ---------- 2) 逐个上传 ----------
for file in "$@"; do
  [ -f "$file" ] || { echo "找不到文件：$file" >&2; exit 1; }
  name=$(basename "$file")
  size=$(wc -c < "$file" | tr -d ' ')
  echo "==> 上传 $name（$size 字节）"

  payload=$(python3 - "$name" "$size" <<'PY'
import json, sys
print(json.dumps({"asset_name": sys.argv[1], "size": int(sys.argv[2]),
                  "overwrite": True, "ttl": 0}))
PY
)
  resp=$(api POST "/$REPO/-/releases/$rid/asset-upload-url" "$payload")
  code=$(code_of "$resp")
  if [ "$code" != "201" ]; then
    echo "取上传地址失败（HTTP $code）：$(body_of "$resp")" >&2
    exit 1
  fi

  upload_url=$(body_of "$resp" | json_get upload_url)
  verify_url=$(body_of "$resp" | json_get verify_url)
  [ -n "$upload_url" ] && [ -n "$verify_url" ] || {
    echo "响应里没有 upload_url / verify_url：$(body_of "$resp")" >&2
    exit 1
  }

  # 直接把文件流给对象存储，不经过 CNB 的 API 网关。
  # 走公司/家用代理时这条链路偶发 CONNECT 502，所以要重试；实在不行可以设
  # CNB_UPLOAD_NOPROXY=1 直连。
  put_opts=(--retry 5 --retry-delay 3 --retry-all-errors --max-time 900)
  [ "${CNB_UPLOAD_NOPROXY:-0}" = "1" ] && put_opts+=(--noproxy '*')

  put_code=$(curl -sS -X PUT --data-binary @"$file" \
    -H "Content-Type: application/octet-stream" \
    "${put_opts[@]}" \
    -o /dev/null -w '%{http_code}' "$upload_url" || echo "000")
  if [ "$put_code" != "200" ] && [ "$put_code" != "201" ]; then
    echo "上传失败（HTTP $put_code）。若是 CONNECT 502，试 CNB_UPLOAD_NOPROXY=1" >&2
    exit 1
  fi

  # 必须确认，不确认的话附件只是躺在存储里，版本页上看不到
  resp=$(api_abs "$verify_url")
  code=$(code_of "$resp")
  if [ "$code" != "200" ]; then
    echo "确认失败（HTTP $code）：$(body_of "$resp")" >&2
    exit 1
  fi
  echo "    完成"
done

# ---------- 3) 把本次构建信息写进版本描述 ----------
# 这步纯粹是好看用的：附件已经传完了，描述没更新不该让整步失败。
if [ -n "${CNB_BODY:-}" ]; then
  if payload=$(python3 - "$NAME" "$BODY" <<'PY'
import json, sys
print(json.dumps({"name": sys.argv[1], "body": sys.argv[2]}, ensure_ascii=True))
PY
  ); then
    api PATCH "/$REPO/-/releases/$rid" "$payload" >/dev/null || echo "    （版本描述没更新成功，不影响附件）"
  else
    echo "    （版本描述没更新成功，不影响附件）"
  fi
fi

echo
echo "版本地址：https://cnb.cool/$REPO/-/releases"
for file in "$@"; do
  name=$(basename "$file")
  echo "直链：https://cnb.cool/$REPO/-/releases/download/$TAG/$name"
done
