extends Node
# BilibiliAPI autoload 门面。
# 只负责：初始化子模块、转发公开接口、转发静态工具、转发旧模块兼容 Callable。
# 业务逻辑一律下沉到子模块，本文件不再写请求细节。

# —— 内部子模块 ——
var _http: BilibiliHttpClient
var _auth: BilibiliAuth
var _user: BilibiliUserApi
var _search: BilibiliSearchApi
var _video: BilibiliVideoApi

# —— 原有依赖 ——
var cover_cache: BilibiliCoverCache
var lyrics_cache: BilibiliLyricsCache
var subtitle_manager: BilibiliSubtitleManager


func _ready() -> void:
	_http = BilibiliHttpClient.new(self)
	_auth = BilibiliAuth.new(_http)
	_user = BilibiliUserApi.new(_http)
	_search = BilibiliSearchApi.new(_http)

	var sub_corr = get_node_or_null("/root/SubtitleCorrection")
	var m4s_player = get_node_or_null("/root/M4SAudioPlayer")

	var api_func = Callable(_http, "request_with_sign")
	var dl_func = Callable(_http, "request")
	cover_cache = BilibiliCoverCache.new(api_func, dl_func)
	lyrics_cache = BilibiliLyricsCache.new()
	subtitle_manager = BilibiliSubtitleManager.new(api_func, dl_func, sub_corr, m4s_player)
	_video = BilibiliVideoApi.new(_http, subtitle_manager)

	if is_instance_valid(sub_corr) and not sub_corr.SubtitleProcessed.is_connected(_on_subtitle_processed):
		sub_corr.SubtitleProcessed.connect(_on_subtitle_processed)

	set_process(true)
	_bootstrap_official_buvid()

func _process(delta: float) -> void:
	if cover_cache:
		cover_cache.update(delta)


func _exit_tree() -> void:
	if cover_cache:
		cover_cache.shutdown()


# ==================== 官方指纹后台拉取 ====================
# 关键：必须是实例函数（非 static），
# 因为 static 函数里的 await 不能可靠挂起，会导致 HTTPRequest 未 ready 就 request → ERR_UNCONFIGURED(3)
func _bootstrap_official_buvid() -> void:
	print("[BilibiliAPI] 正在通过 HTTPRequest 获取官方 buvid3...")

	var http := HTTPRequest.new()
	add_child(http)

	# 等两帧，确保 HTTPRequest 进入树并 ready
	await get_tree().process_frame
	await get_tree().process_frame

	http.timeout = 10.0

	var headers := PackedStringArray([
		"User-Agent: " + BilibiliCookieStore.get_dynamic_user_agent(),
		"Referer: https://www.bilibili.com",
		"Origin: https://www.bilibili.com",
		"Accept: application/json, text/plain, */*"
	])

	var max_attempts := 3
	for attempt in range(1, max_attempts + 1):
		var err := http.request(
			"https://api.bilibili.com/x/frontend/finger/spi",
			headers,
			HTTPClient.METHOD_GET
		)
		if err != OK:
			print("[BilibiliAPI] ❌ request() 返回错误码: ", err, "  (3=ERR_UNCONFIGURED)")
			await get_tree().process_frame
			continue

		var result: Array = await http.request_completed
		var code: int = result[1]
		var body: PackedByteArray = result[3]

		if code != 200 or body.size() == 0:
			print("[BilibiliAPI] ❌ 指纹接口响应无效, code: ", code)
			continue

		var json := JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			continue

		var data = json.get_data()
		if typeof(data) != TYPE_DICTIONARY or data.get("code", -1) != 0:
			continue

		var payload: Dictionary = data.get("data", {})
		var b_3: String = payload.get("b_3", "")
		var b_4: String = payload.get("b_4", "")

		if b_3.is_empty():
			continue

		BilibiliCookieStore.set_official_buvid(b_3, b_4)
		print("[BilibiliAPI] ✅ 官方 buvid3 = ", b_3)
		http.queue_free()
		return

	print("[BilibiliAPI] ❌ 多次尝试后仍未获得官方指纹，将使用本地兜底")
	http.queue_free()


# ==================== 公开 API（转发） ====================

func fetch_user_info_by_mid(mid: String, callback: Callable, max_retries: int = 3) -> void:
	_user.fetch_by_mid(mid, callback, max_retries)


func fetch_user_videos(mid: String, callback: Callable, page: int = 1, page_size: int = 20, order = "pubdate") -> void:
	_user.fetch_videos(mid, callback, page, page_size, order)


func fetch_user_videos_medialist(mid: String, username: String, callback: Callable, page: int = 1, page_size: int = 20) -> void:
	_user.fetch_videos_medialist(mid, username, callback, page, page_size)


func search_bilibili(callback: Callable, keyword: String, num: int = 10, order = 0, page := 1, author: String = "", _tids := 3) -> void:
	_search.search(callback, keyword, num, order, page, author, _tids)


func fetch_cover(link: String, callback: Callable, width: int = 160, height: int = 160) -> void:
	cover_cache.fetch_cover(link, callback, width, height)


func fetch_video_info(bvid: String, callback: Callable) -> void:
	_video.fetch_info(bvid, callback)


func fetch_subtitle_auto(bvid: String, callback: Callable, save_path: String = "") -> void:
	_video.fetch_subtitle_auto(bvid, callback, save_path)


func fetch_subtitle_with_info(info: Dictionary, callback: Callable, save_path: String = "") -> void:
	_video.fetch_subtitle_with_info(info, callback, save_path)


func start_qr_login(login_callback: Callable) -> void:
	_auth.start_login(login_callback)


func fetch_user_avatar(callback: Callable) -> void:
	_auth.fetch_avatar(callback)


func get_csrf() -> String:
	return _auth.get_csrf()


# ==================== 静态工具转发 ====================

static func get_dynamic_user_agent() -> String:
	return BilibiliCookieStore.get_dynamic_user_agent()

static func get_or_generate_buvid() -> String:
	return BilibiliCookieStore.get_or_generate_buvid()

static func generate_fingerprint_buvid() -> String:
	return BilibiliCookieStore.generate_fingerprint_buvid()

static func generate_fake_b_nut() -> String:
	return BilibiliCookieStore.generate_fake_b_nut()

static func decode_html_entities(text: String) -> String:
	return BilibiliHTMLDecoder.decode(text)

func bv_to_aid(bvid: String) -> int:
	return BilibiliBvCodec.bv_to_aid(bvid)


# ==================== 兼容旧模块的 Callable 接口 ====================

func _request(url: String, callback: Callable, extra = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = PackedStringArray(), mid: int = 0) -> void:
	_http.request(url, callback, extra, method, custom_headers, mid)


func _request_with_sign(url: String, callback: Callable, extra = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = PackedStringArray(), mid: int = 0) -> void:
	_http.request_with_sign(url, callback, extra, method, custom_headers, mid)


func _get_image_headers() -> PackedStringArray:
	return _http.get_image_headers()


# ==================== 信号回调 ====================

func _on_subtitle_processed(lrc_path: String, request_id: String) -> void:
	subtitle_manager.handle_correction_result(request_id, lrc_path)


## 获取当前登录用户的云端收藏夹列表
func fetch_fav_folders(callback: Callable) -> void:
	_user.fetch_fav_folders(callback)

## 获取指定云端收藏夹的内容（单页）
func fetch_fav_items(media_id: int, callback: Callable, pn: int = 1, ps: int = 20) -> void:
	_user.fetch_fav_items(media_id, pn, ps, callback)

## 获取指定云端收藏夹的全部内容（自动翻页）
func fetch_all_fav_items(media_id: int, callback: Callable) -> void:
	_user.fetch_all_fav_items(media_id, callback)

func fetch_audio_url(bvid: String, cid: int, callback: Callable) -> void:
	_video.fetch_audio_url(bvid, cid, callback)
