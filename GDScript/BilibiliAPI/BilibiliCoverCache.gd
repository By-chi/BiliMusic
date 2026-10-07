class_name BilibiliCoverCache

# 封面缓存：内存索引 + 磁盘缓存 + 网络回源下载。
# 依赖注入两个请求函数（api_request_func / download_request_func），
# 均来自 BilibiliHttpClient，本类不再直接 new HTTPRequest。

## 注入：B站 API 请求（需 WBI 签名）、普通下载请求（图片/字幕）
var _api_request_func: Callable
var _download_request_func: Callable

var _index := {}
var _index_loaded := false
var _load_queue := []
var _last_process_time := 0
var _processing_active := false
var _save_thread: Thread = null
var _save_queue := []
var _save_mutex := Mutex.new()
var _save_semaphore := Semaphore.new()
var _stop_save_thread := false
var _deferred_updates := []
var _deferred_mutex := Mutex.new()
var _by_link: Dictionary = {}   # link -> Array[exact_key]
var _lru: Array = []            # FIFO 顺序

func _init(api_request_func: Callable, download_request_func: Callable) -> void:
	_api_request_func = api_request_func
	_download_request_func = download_request_func
	_save_thread = Thread.new()
	_save_thread.start(_save_worker)

func shutdown() -> void:
	_stop_save_thread = true
	_save_semaphore.post()
	if _save_thread and _save_thread.is_alive():
		_save_thread.wait_to_finish()

func update(_delta: float) -> bool:
	var has_work = false
	_deferred_mutex.lock()
	if not _deferred_updates.is_empty():
		var updates = _deferred_updates.duplicate()
		_deferred_updates.clear()
		_deferred_mutex.unlock()
		for task in updates:
			_add_to_index(task.link, task.width, task.height, task.filename)
		has_work = true
	else:
		_deferred_mutex.unlock()
	var processed_this_frame := 0
	while not _load_queue.is_empty():
		var head = _load_queue[0]
		var is_cache_hit: bool = head.get("cached_path", "") != ""
		if is_cache_hit:
			# 缓存命中：每帧最多批量处理 N 条
			if processed_this_frame >= BilibiliConstants.CACHE_HIT_BURST_PER_FRAME:
				break
			_process_one_task()
			processed_this_frame += 1
			has_work = true
		else:
			# 网络任务：维持原节流
			var now = Time.get_ticks_msec()
			if now - _last_process_time >= BilibiliConstants.CACHE_LOAD_COOLDOWN_MS:
				_last_process_time = now
				_process_one_task()
				has_work = true
			break

	if not _load_queue.is_empty():
		has_work = true
	return has_work

# ==================================================================
# fetch_cover：
#   缓存命中：入队，由 update() 节流处理
#   缓存未命中：立刻走网络下载
# ==================================================================
func fetch_cover(link: String, callback: Callable, width: int = 160, height: int = 160) -> void:
	var cached_path := _get_cached_file(link, width, height)
	if not cached_path.is_empty():
		_load_queue.push_back({
			"link": link,
			"width": width,
			"height": height,
			"callback": callback,
			"cached_path": cached_path
		})
		if _load_queue.size() >= BilibiliConstants.CACHE_QUEUE_MAX_SIZE:
			flush()
		return

	# 缓存未命中 → 走网络
	_get_cover_url(link, width, height, func(url):
		if url.is_empty():
			GdScriptFunc.safe_callback(link, null, callback)
			return
		_download_cover(url, link, width, height, callback)
	)

func flush() -> void:
	while not _load_queue.is_empty():
		_process_one_task()
	_last_process_time = Time.get_ticks_msec()

# 使用 API 请求函数获取封面地址
func _get_cover_url(bvid: String, width: int, height: int, next: Callable) -> void:
	var url = "https://api.bilibili.com/x/web-interface/view?bvid=" + bvid
	_api_request_func.call(url, _on_cover_url_received, [bvid, width, height, next])

func _on_cover_url_received(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var arr: Array = extra
	var bvid: String = arr[0]
	var width: int = arr[1]
	var height: int = arr[2]
	var next: Callable = arr[3]

	if result != HTTPRequest.RESULT_SUCCESS or response_code != 200:
		push_error("获取视频信息失败 (%s): %d" % [bvid, response_code])
		next.call("")
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败 (%s)" % bvid)
		next.call("")
		return

	var data = json.get_data()
	if data.get("code") != 0:
		push_error("API错误 (%s): %s" % [bvid, data.get("message")])
		next.call("")
		return

	var pic = data.get("data", {}).get("pic", "")
	if pic.is_empty():
		push_error("未找到封面URL (%s)" % bvid)
		next.call("")
		return

	next.call(pic + "@%dw_%dh_1c.jpg" % [width, height])

# 使用普通下载请求函数下载图片（统一取用图片下载头）
func _download_cover(url: String, bvid: String, width: int, height: int, callback: Callable) -> void:
	var headers = [
		"User-Agent: " + BilibiliCookieStore.get_dynamic_user_agent(),
		"Referer: https://www.bilibili.com"
	]
	_download_request_func.call(url, _on_cover_downloaded, [bvid, width, height, callback], HTTPClient.METHOD_GET, headers)

func _on_cover_downloaded(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var arr: Array = extra
	var bvid: String = arr[0]
	var width: int = arr[1]
	var height: int = arr[2]
	var callback: Callable = arr[3]

	if result != HTTPRequest.RESULT_SUCCESS or response_code != 200:
		push_error("下载封面失败 (%s): %d" % [bvid, response_code])
		GdScriptFunc.safe_callback(bvid, null, callback)
		return

	var img = Image.new()
	if img.load_jpg_from_buffer(body) != OK and img.load_png_from_buffer(body) != OK:
		push_error("图片解析失败 (%s)" % bvid)
		GdScriptFunc.safe_callback(bvid, null, callback)
		return

	var tex = ImageTexture.create_from_image(img)
	GdScriptFunc.safe_callback(bvid, tex, callback)

	_save_mutex.lock()
	_save_queue.push_back({"link": bvid, "width": width, "height": height, "image_data": body})
	_save_mutex.unlock()
	_save_semaphore.post()

# ==================================================================
# 只选"实际尺寸 >= 请求尺寸"里面积最小的缓存
#   索引里记录的 width/height 是磁盘文件的真实尺寸（由 _save_worker 保证）
# ==================================================================
func _get_cached_file(link: String, width: int, height: int) -> String:
	_load_index()
	var candidates: Array = _by_link.get(link, [])
	var best_key := ""
	var best_area := INF
	for k in candidates:
		if not _index.has(k):
			continue
		var e = _index[k]
		if e.width < width or e.height < height:
			continue
		var area: int = e.width * e.height
		if area < best_area:
			best_area = area
			best_key = k
	if best_key != "":
		var path = BilibiliConstants.CACHE_DIR.path_join(_index[best_key].file)
		if FileAccess.file_exists(path):
			return path
		_remove_key(best_key)
	return ""

func _remove_key(key: String) -> void:
	if not _index.has(key):
		return
	var link: String = _index[key].link
	if _by_link.has(link):
		_by_link[link].erase(key)
		if _by_link[link].is_empty():
			_by_link.erase(link)
	_index.erase(key)

func _evict_fifo() -> void:
	var to_remove := _index.size() - BilibiliConstants.MAX_CACHE_SIZE
	var i := 0
	while to_remove > 0 and i < _lru.size():
		var key: String = _lru[i]
		i += 1
		if _index.has(key):
			var old_file = _index[key].file
			var old_path = BilibiliConstants.CACHE_DIR.path_join(old_file)
			if FileAccess.file_exists(old_path):
				DirAccess.remove_absolute(old_path)
			_remove_key(key)
			to_remove -= 1
	_lru = _lru.slice(i)

func _add_to_index(link: String, width: int, height: int, filename: String) -> void:
	_load_index()
	var key = _cache_key(link, width, height)
	var now = Time.get_unix_time_from_system()
	_index[key] = {
		"file": filename, "time": now, "link": link, "width": width, "height": height
	}
	if not _by_link.has(link):
		_by_link[link] = []
	_by_link[link].append(key)
	_lru.append(key)

	if _index.size() > BilibiliConstants.MAX_CACHE_SIZE:
		_evict_fifo()
	_save_index()

func _load_index() -> void:
	if _index_loaded:
		return

	# ==================================================================
	# 一次性清理旧版脏索引（旧版记录的是"请求尺寸"，与文件实际不符）
	# ==================================================================
	const INDEX_VERSION_KEY := "_index_version"
	const CURRENT_INDEX_VERSION := 2
	var stored_version: int = GdScriptFunc.get_data("CoverCacheMeta", INDEX_VERSION_KEY, 0)
	if stored_version < CURRENT_INDEX_VERSION:
		var old_keys = GdScriptFunc.get_keys("CoverCache")
		for k in old_keys:
			GdScriptFunc.remove_key("CoverCache", k)
		GdScriptFunc.set_data("CoverCacheMeta", INDEX_VERSION_KEY, CURRENT_INDEX_VERSION, true)
		var dir := DirAccess.open(BilibiliConstants.CACHE_DIR)
		if dir:
			dir.list_dir_begin()
			var fn := dir.get_next()
			while fn != "":
				if not dir.current_is_dir() and fn.ends_with(".jpg"):
					dir.remove(fn)
				fn = dir.get_next()
			dir.list_dir_end()
		print("[BilibiliCoverCache] 已清理旧版封面缓存索引和文件")

	var keys = GdScriptFunc.get_keys("CoverCache")
	var times := []
	for key in keys:
		var entry = GdScriptFunc.get_data("CoverCache", key)
		if typeof(entry) == TYPE_DICTIONARY:
			var file = entry.get("file", "")
			if file.is_empty():
				continue
			var link = entry.get("link", "")
			var w = entry.get("width", 0)
			var h = entry.get("height", 0)
			var t = entry.get("time", 0)
			_index[key] = {"file": file, "time": t, "link": link, "width": w, "height": h}
			if not _by_link.has(link):
				_by_link[link] = []
			_by_link[link].append(key)
			times.append({"k": key, "t": t})
	times.sort_custom(func(a, b): return a.t < b.t)
	_lru = times.map(func(x): return x.k)
	_index_loaded = true

func _save_index() -> void:
	var old = GdScriptFunc.get_keys("CoverCache")
	for k in old:
		GdScriptFunc.remove_key("CoverCache", k)
	for k in _index:
		GdScriptFunc.set_data("CoverCache", k, _index[k])

func _cache_key(link: String, width: int, height: int) -> String:
	return "%s_%dx%d" % [link, width, height]

func _evict() -> void:
	var sorted = []
	for k in _index:
		sorted.append({"key": k, "time": _index[k].time})
	sorted.sort_custom(func(a, b): return a.time < b.time)
	var remove_count = _index.size() - BilibiliConstants.MAX_CACHE_SIZE
	for i in range(remove_count):
		var item = sorted[i]
		var old_file = _index[item.key].file
		var old_path = BilibiliConstants.CACHE_DIR.path_join(old_file)
		if FileAccess.file_exists(old_path):
			DirAccess.remove_absolute(old_path)
		_index.erase(item.key)

func _process_one_task() -> void:
	if _load_queue.is_empty():
		return
	var task = _load_queue.pop_front()
	var link: String = task.link
	var callback: Callable = task.callback
	var cached_path: String = task.cached_path
	var req_width: int = task.width
	var req_height: int = task.height

	if not FileAccess.file_exists(cached_path):
		push_error("缓存文件丢失，重新下载 (%s)" % link)
		_get_cover_url(link, req_width, req_height, func(url):
			if url.is_empty():
				GdScriptFunc.safe_callback(link, null, callback)
				return
			_download_cover(url, link, req_width, req_height, callback)
		)
		return

	var img = Image.new()
	if img.load(cached_path) != OK:
		push_error("缓存图片损坏，重新下载 (%s)" % link)
		DirAccess.remove_absolute(cached_path)
		_get_cover_url(link, req_width, req_height, func(url):
			if url.is_empty():
				GdScriptFunc.safe_callback(link, null, callback)
				return
			_download_cover(url, link, req_width, req_height, callback)
		)
		return

	if img.get_width() < req_width or img.get_height() < req_height:
		push_warning("[缓存] 实际尺寸 %dx%d 小于请求 %dx%d，丢弃并重新下载: %s"
			% [img.get_width(), img.get_height(), req_width, req_height, link])
		DirAccess.remove_absolute(cached_path)
		_get_cover_url(link, req_width, req_height, func(url):
			if url.is_empty():
				GdScriptFunc.safe_callback(link, null, callback)
				return
			_download_cover(url, link, req_width, req_height, callback)
		)
		return

	# 尺寸匹配则直接使用，否则等比缩小并中心裁剪
	if img.get_width() != req_width or img.get_height() != req_height:
		img = _resize_and_crop_center(img, req_width, req_height)

	var tex = ImageTexture.create_from_image(img)
	GdScriptFunc.safe_callback(link, tex, callback)

# 只缩不放
func _resize_and_crop_center(src: Image, target_width: int, target_height: int) -> Image:
	var sw = src.get_width()
	var sh = src.get_height()
	if sw <= 0 or sh <= 0:
		return src
	if sw < target_width or sh < target_height:
		push_warning("[缓存] 源图 %dx%d 小于目标 %dx%d，跳过缩放直接返回"
			% [sw, sh, target_width, target_height])
		return src

	var scale = max(float(target_width) / sw, float(target_height) / sh)
	var new_w = int(sw * scale)
	var new_h = int(sh * scale)

	src.resize(new_w, new_h, Image.INTERPOLATE_LANCZOS)

	@warning_ignore("integer_division")
	var crop_x = (new_w - target_width) / 2
	@warning_ignore("integer_division")
	var crop_y = (new_h - target_height) / 2
	var rect = Rect2i(crop_x, crop_y, target_width, target_height)

	return src.get_region(rect)

func _save_worker() -> void:
	while not _stop_save_thread:
		_save_semaphore.wait()
		if _stop_save_thread:
			break
		_save_mutex.lock()
		if _save_queue.is_empty():
			_save_mutex.unlock()
			continue
		var task = _save_queue.pop_front()
		_save_mutex.unlock()

		# 用实际图片尺寸作为索引键，避免"索引记 240×240、磁盘是 100×100"这类错乱
		var probe := Image.new()
		if probe.load_jpg_from_buffer(task.image_data) != OK \
				and probe.load_png_from_buffer(task.image_data) != OK:
			push_error("[后台] 无法解析封面图片数据: " + task.link)
			continue
		var real_w := probe.get_width()
		var real_h := probe.get_height()
		if real_w <= 0 or real_h <= 0:
			push_error("[后台] 封面尺寸无效: " + task.link)
			continue

		var filename = _get_cache_filename(task.link, real_w, real_h)
		var file_path = BilibiliConstants.CACHE_DIR.path_join(filename)
		var dir = DirAccess.open(BilibiliConstants.CACHE_DIR)
		if not dir:
			DirAccess.make_dir_recursive_absolute(BilibiliConstants.CACHE_DIR)
		var file = FileAccess.open(file_path, FileAccess.WRITE)
		if file:
			file.store_buffer(task.image_data)
			file.close()
			_deferred_mutex.lock()
			_deferred_updates.push_back({
				"link": task.link,
				"width": real_w,
				"height": real_h,
				"filename": filename
			})
			_deferred_mutex.unlock()
		else:
			push_error("[后台] 写入封面缓存失败: " + file_path)

static func _get_cache_filename(link: String, width: int, height: int) -> String:
	return ("%s_%dx%d" % [link, width, height]).md5_text() + ".jpg"
