
## `demo_auto_failover.sh` — Giải thích từng dòng

---

### Shebang & safety flags

```bash
#!/usr/bin/env bash        # Dùng bash (tìm trong PATH, portable hơn #!/bin/bash)
set -euo pipefail
#  -e  → thoát ngay khi có lệnh nào trả về exit code ≠ 0
#  -u  → lỗi ngay nếu dùng biến chưa khai báo (undefined variable)
#  -o pipefail → nếu 1 lệnh trong pipe thất bại, toàn bộ pipe coi là thất bại
#                (default bash chỉ lấy exit code của lệnh cuối pipe)
```

---

### Hàm `cluster()`
> Lấy trạng thái cluster từ node Patroni đầu tiên còn sống.

```bash
cluster() {
  for c in patroni1 patroni2 patroni3; do   # Thử lần lượt 3 node
    local out                                # Biến local, chỉ tồn tại trong hàm
    out="$(docker exec "$c" patronictl list 2>/dev/null || true)"
    #   docker exec "$c" patronictl list → chạy lệnh patronictl list bên trong container $c
    #   2>/dev/null  → bỏ qua stderr (nếu container đã chết, error sẽ không in ra)
    #   || true      → nếu lệnh fail, vẫn không bị set -e thoát script

    if [[ -n "$out" ]]; then   # Nếu output không rỗng (node còn sống, có kết quả)
      printf '%s\n' "$out"     # In kết quả ra stdout
      return 0                 # Thành công, thoát hàm ngay
    fi
  done
  echo "No reachable Patroni node for patronictl list" >&2   # In lỗi ra stderr
  return 1   # Không tìm được node nào → thất bại
}
```

---

### Hàm `leader_member()`
> Trả về tên container của node đang là Leader.

```bash
leader_member() {
  cluster | awk -F'|' '$4 ~ /Leader/ {gsub(/ /, "", $2); print $2; exit}'
  # cluster     → gọi hàm ở trên, lấy output dạng bảng patronictl list
  # awk -F'|'   → tách cột bằng dấu '|' (vì patronictl dùng bảng ASCII kiểu | col1 | col2 |)
  # $4 ~ /Leader/         → lọc dòng nào có cột 4 chứa chữ "Leader"
  # gsub(/ /, "", $2)     → xóa toàn bộ khoảng trắng trong cột 2 (Member name)
  # print $2              → in tên member (ví dụ: patroni1, patroni2)
  # exit                  → thoát awk sau khi in dòng đầu tiên khớp
}
```

---

### Hàm `wait_new_leader(old_leader)`
> Chờ tối đa 45×2 = 90 giây cho đến khi có leader MỚI khác leader cũ.

```bash
wait_new_leader() {
  local old_leader="$1"       # Nhận tên leader cũ qua tham số $1
  for _ in {1..45}; do        # Lặp 45 lần (dùng _ vì không cần biến đếm)
    local now
    now="$(leader_member || true)"   # Lấy leader hiện tại; || true tránh set -e dừng script
    if [[ -n "$now" && "$now" != "$old_leader" ]]; then
    # -n "$now"              → leader mới không rỗng (đã bầu xong)
    # "$now" != "$old_leader" → leader mới khác leader cũ
      echo "$now"    # In tên leader mới
      return 0       # Thành công
    fi
    sleep 2          # Chờ 2 giây rồi thử lại
  done
  return 1           # Hết 90 giây vẫn chưa có leader mới → thất bại
}
```

---

### Hàm `wait_streaming_zero_lag(member)`
> Chờ node `member` rejoin cluster và đạt trạng thái `streaming` với lag = 0 MB.

```bash
wait_streaming_zero_lag() {
  local member="$1"        # Tên container cần theo dõi
  for _ in {1..45}; do     # Tối đa 90 giây
    local state_lag
    state_lag="$(cluster | awk -F'|' -v m="$member" '$2 ~ m {gsub(/ /, "", $5); gsub(/ /, "", $7); print $5 "," $7; exit}')"
    # awk -v m="$member"    → truyền biến shell vào awk
    # $2 ~ m                → lọc dòng có cột 2 (Member) khớp với tên member
    # $5 = cột State (running/streaming/stopped...)
    # $7 = cột Lag in MB
    # gsub(/ /, "", ...)    → xóa khoảng trắng
    # print $5 "," $7       → in dạng "streaming,0" hoặc "streaming,1" ...

    echo "[$(date +%H:%M:%S)] $member: ${state_lag:-missing}"
    # date +%H:%M:%S        → timestamp dạng HH:MM:SS
    # ${state_lag:-missing} → nếu state_lag rỗng, in "missing"

    if [[ "$state_lag" == "streaming,0" ]]; then   # Đạt mục tiêu
      return 0
    fi
    sleep 2
  done
  return 1   # Timeout
}
```

---

### Phần chính — Step 1: Trigger auto failover

```bash
echo "== BEFORE =="
cluster                              # In trạng thái cluster trước khi dừng node

old_leader="$(leader_member)"        # Lưu tên leader hiện tại
echo "Old leader: $old_leader"
docker stop "$old_leader" >/dev/null # Dừng container leader → trigger Patroni bầu leader mới
                                     # >/dev/null → bỏ output "container stopped"

new_leader="$(wait_new_leader "$old_leader")"   # Chờ leader mới được bầu
echo "New leader: $new_leader"
echo "== AFTER FAILOVER =="
cluster                              # In trạng thái cluster sau failover
```

---

### Phần chính — Step 2: Kiểm tra write qua HAProxy (optional)

```bash
if [[ "${SKIP_WRITE_CHECK:-0}" != "1" ]]; then
# ${SKIP_WRITE_CHECK:-0} → nếu biến không được set, mặc định là "0"
# Cho phép skip bước này bằng: SKIP_WRITE_CHECK=1 ./demo_auto_failover.sh

  echo "Write check via HAProxy :5000"
  if ! curl -fsS -X POST https://localhost:7134/products \
    -H "Content-Type: application/json" \
    -d '{"name":"After failover","price":9.99}'; then
  # curl -f  → fail với exit code ≠ 0 nếu HTTP response là 4xx/5xx
  # curl -s  → silent (không in progress bar)
  # curl -S  → vẫn in lỗi khi dùng -s (kết hợp -sS: im lặng nhưng vẫn báo lỗi)
  # -X POST  → HTTP method POST
  # -d '...' → request body JSON
  # if !     → nếu curl thất bại (write đến HAProxy bị lỗi)
    echo "Write check failed (app API may not be running on :5050)." >&2
  fi
  echo   # In dòng trống cho đẹp
fi
```

---

### Phần chính — Step 3: Cho node cũ rejoin & chờ sync

```bash
echo "Rejoin old leader: $old_leader"
docker start "$old_leader" >/dev/null   # Khởi động lại container cũ
                                         # Patroni tự detect, sync WAL, rồi trở thành Replica

echo "Wait until rejoined node reaches streaming, lag=0"
wait_streaming_zero_lag "$old_leader" || true
# || true → dù timeout cũng không dừng script (set -e không kill ở đây)

echo "Patroni /replica on rejoined node (expected 200 once healthy):"
docker exec "$old_leader" sh -c 'curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8008/replica'
# Gọi Patroni REST API endpoint /replica bên trong container
# -o /dev/null → bỏ body response, chỉ lấy HTTP status code
# -w "%{http_code}\n" → in HTTP code (200 = node sẵn sàng nhận read)
# 200 → node healthy, HAProxy sẽ route read traffic vào đây
# 503 → node chưa sẵn sàng

echo "== FINAL =="
cluster   # In trạng thái cluster cuối cùng sau khi mọi thứ ổn định
```
