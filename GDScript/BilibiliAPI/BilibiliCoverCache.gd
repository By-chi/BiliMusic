class_name BilibiliCoverCache

# 注入两种请求函数：一个用于 B站 API（需要 WBI 签名），一个用于普通下载（图片/字幕）
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

	var now = Time.get_ticks_msec()
	if not _load_queue.is_empty() and now - _last_process_time >= BilibiliConstants.CACHE_LOAD_COOLDOWN_MS:
		_last_process_time = now
		_process_one_task()
		has_work = true
	if not _load_queue.is_empty():
		has_work = true
	return has_work

func fetch_cover(link: String, callback: Callable, width: int = 160, height: int = 160) -> void:
	var cached_path := _get_cached_file(link, width, height)
	if not cached_path.is_empty():
		_load_queue.push_back({"link": link, "width": width, "height": height, "callback": callback, "cached_path": cached_path})
		if _load_queue.size() >= BilibiliConstants.CACHE_QUEUE_MAX_SIZE:
			flush()
		else:
			_processing_active = true
			_last_process_time = Time.get_ticks_msec()
		return
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

# 使用普通下载请求函数下载图片
func _download_cover(url: String, bvid: String, width: int, height: int, callback: Callable) -> void:
	var headers = [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
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

func _get_cached_file(link: String, width: int, height: int) -> String:
	_load_index()
	var exact_key = _cache_key(link, width, height)
	#精确命中
	if _index.has(exact_key):
		var entry: Dictionary = _index[exact_key]
		var path = BilibiliConstants.CACHE_DIR.path_join(entry.file)
		if FileAccess.file_exists(path):
			return path
		else:
			_index.erase(exact_key)
			_save_index()

	#找 link 相同且宽高均 >= 请求尺寸的最小图片
	var best_entry = null
	var best_area = INF
	for key in _index:
		var entry = _index[key]
		if entry.get("link", "") != link:
			continue
		if entry.width >= width and entry.height >= height:
			var area = entry.width * entry.height
			if area < best_area:
				best_area = area
				best_entry = entry

	if best_entry != null:
		var path = BilibiliConstants.CACHE_DIR.path_join(best_entry.file)
		if FileAccess.file_exists(path):
			return path
		else:
			# 文件丢失，清理该索引
			var missing_key = _cache_key(link, best_entry.width, best_entry.height)
			_index.erase(missing_key)
			_save_index()
	return ""

func _add_to_index(link: String, width: int, height: int, filename: String) -> void:
	_load_index()
	var key = _cache_key(link, width, height)
	var now = Time.get_unix_time_from_system()
	_index[key] = {
		"file": filename,
		"time": now,
		"link": link,
		"width": width,
		"height": height
	}
	if _index.size() > BilibiliConstants.MAX_CACHE_SIZE:
		_evict()
	_save_index()

func _load_index() -> void:
	if _index_loaded:
		return
	var keys = GdScriptFunc.get_keys("CoverCache")
	for key in keys:
		var entry = GdScriptFunc.get_data("CoverCache", key)
		if typeof(entry) == TYPE_DICTIONARY:
			var file = entry.get("file", "")
			if file.is_empty():
				continue
			# 兼容旧数据，缺失字段给默认值（宽高为0，导致大带小失效但不报错）
			var time = entry.get("time", 0)
			var link = entry.get("link", "")
			var width = entry.get("width", 0)
			var height = entry.get("height", 0)
			_index[key] = {
				"file": file,
				"time": time,
				"link": link,
				"width": width,
				"height": height
			}
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

	# 尺寸匹配则直接使用，否则等比缩放并中心裁剪至请求尺寸
	if img.get_width() != req_width or img.get_height() != req_height:
		img = _resize_and_crop_center(img, req_width, req_height)

	var tex = ImageTexture.create_from_image(img)
	GdScriptFunc.safe_callback(link, tex, callback)
# 等比缩放至完全覆盖目标区域，然后中心裁剪
func _resize_and_crop_center(src: Image, target_width: int, target_height: int) -> Image:
	var sw = src.get_width()
	var sh = src.get_height()
	var scale = max(float(target_width) / sw, float(target_height) / sh)
	var new_w = int(sw * scale)
	var new_h = int(sh * scale)

	# 先等比放大到完全覆盖目标尺寸
	src.resize(new_w, new_h, Image.INTERPOLATE_LANCZOS)  # 若报错可换成 Image.INTERPOLATE_BILINEAR

	# 计算中心裁剪区域
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

		var filename = _get_cache_filename(task.link, task.width, task.height)
		var file_path = BilibiliConstants.CACHE_DIR.path_join(filename)
		var dir = DirAccess.open(BilibiliConstants.CACHE_DIR)
		if not dir:
			DirAccess.make_dir_recursive_absolute(BilibiliConstants.CACHE_DIR)
		var file = FileAccess.open(file_path, FileAccess.WRITE)
		if file:
			file.store_buffer(task.image_data)
			file.close()
			_deferred_mutex.lock()
			_deferred_updates.push_back({"link": task.link, "width": task.width, "height": task.height, "filename": filename})
			_deferred_mutex.unlock()
		else:
			push_error("[后台] 写入封面缓存失败: " + file_path)

static func _get_cache_filename(link: String, width: int, height: int) -> String:
	return ("%s_%dx%d" % [link, width, height]).md5_text() + ".jpg"
