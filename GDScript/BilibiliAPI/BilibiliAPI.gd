extends Node

var cover_cache: BilibiliCoverCache
var lyrics_cache: BilibiliLyricsCache
var subtitle_manager: BilibiliSubtitleManager

var _wbi_key_cache := {"img_key": "", "sub_key": "", "cached_time": 0}
var _video_info_cache := {}

# 二维码登录相关
var qr_window: Window = null
var on_qr_login_result: Callable
var _poll_timer: Timer
var _close_delay_timer: Timer

# 静态变量
static var _cached_buvid: String = ""


func _ready() -> void:
	var sub_corr = get_node_or_null("/root/SubtitleCorrection")
	var m4s_player = get_node_or_null("/root/M4SAudioPlayer")

	var api_func = Callable(self, "_request_with_sign")
	var dl_func  = Callable(self, "_request")
	cover_cache = BilibiliCoverCache.new(api_func, dl_func)
	lyrics_cache = BilibiliLyricsCache.new()
	subtitle_manager = BilibiliSubtitleManager.new(api_func, dl_func, sub_corr, m4s_player)

	if is_instance_valid(sub_corr) and not sub_corr.SubtitleProcessed.is_connected(_on_subtitle_processed):
		sub_corr.SubtitleProcessed.connect(_on_subtitle_processed)

	set_process(true)

func _process(delta: float) -> void:
	if cover_cache:
		cover_cache.update(delta)

func _exit_tree() -> void:
	if cover_cache:
		cover_cache.shutdown()


func _get_headers() -> PackedStringArray:
	return _get_headers_with_mid(0)

func _get_headers_with_mid(mid: int = 0) -> PackedStringArray:
	var cookies = [
		"buvid3=" + get_or_generate_buvid(),
		"buvid4=" + _get_or_generate_cookie_field("buvid4", Callable(self, "_generate_buvid4")),
		"b_nut=" + generate_fake_b_nut(),
		"rpdid=" + _get_or_generate_cookie_field("rpdid", Callable(self, "_generate_rpdid")),
		"_uuid=" + _get_or_generate_cookie_field("_uuid", func():
			return _random_string(8).to_upper() + "-" + _random_string(4) + "-" + _random_string(4) + "-" + _random_string(4) + "-" + _random_string(12).to_upper() + "infoc"
			),
		"theme-tip-show=SHOWED",
		"theme-avatar-tip-show=SHOWED",
		"theme-switch-show=SHOWED",
		"theme_style=dark",
		"hit-dyn-v2=1",
		"buvid_fp_plain=undefined",
		"LIVE_BUVID=AUTO" + str(Time.get_unix_time_from_system()) + "411",
		"fingerprint=" + _get_or_generate_cookie_field("fingerprint", Callable(self, "_generate_fingerprint")),
		"buvid_fp=" + _get_or_generate_cookie_field("buvid_fp", Callable(self, "_generate_fingerprint")),
		"PVID=1",
		"ogv_device_support_dolby=0",
		"ogv_device_support_hdr=0",
		"browser_resolution=" + str(DisplayServer.screen_get_size().x) + "-" + str(DisplayServer.screen_get_size().y),
		"home_feed_column=4",
		"b_lsid=" + _get_or_generate_cookie_field("b_lsid", Callable(self, "_generate_b_lsid"))
	]

	var sess = GdScriptFunc.get_data("AccountData", "SESSDATA", "")
	if sess != "": cookies.append("SESSDATA=" + sess)
	var jct = GdScriptFunc.get_data("AccountData", "bili_jct", "")
	if jct != "": cookies.append("bili_jct=" + jct)
	var uid = GdScriptFunc.get_data("AccountData", "DedeUserID", "")
	if uid != "": cookies.append("DedeUserID=" + uid)
	var uidmd5 = GdScriptFunc.get_data("AccountData", "DedeUserID__ckMd5", "")
	if uidmd5 != "": cookies.append("DedeUserID__ckMd5=" + uidmd5)
	var sid = GdScriptFunc.get_data("AccountData", "sid", "")
	if sid != "": cookies.append("sid=" + sid)
	var bp = GdScriptFunc.get_data("AccountData", "bp_t_offset", "")
	if bp != "":
		cookies.append("bp_t_offset_" + uid + "=" + bp)

	var cookie = "; ".join(cookies) + ";"
	var referer = "https://space.bilibili.com/"
	if mid != 0:
		referer += str(mid) + "/upload/video"

	return [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: " + referer,
		"Origin: https://space.bilibili.com",
		"Accept: application/json, text/plain, */*",
		"Accept-Language: zh-CN,zh;q=0.9,en;q=0.8",
		'Sec-Ch-Ua: "Not;A=Brand";v="8", "Chromium";v="120", "Microsoft Edge";v="120"',
		"Sec-Ch-Ua-Mobile: ?0",
		'Sec-Ch-Ua-Platform: "Windows"',
		"Sec-Fetch-Dest: empty",
		"Sec-Fetch-Mode: cors",
		"Sec-Fetch-Site: same-site",
		"Dnt: 1",
		"Priority: u=1, i",
		"Cookie: " + cookie
	]

func _get_image_headers() -> PackedStringArray:
	return [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: https://www.bilibili.com"
	]

func _request(url: String, callback: Callable, extra: Variant = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = _get_headers(), mid: int = 0) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	var headers = custom_headers
	if mid != 0 and custom_headers == _get_headers():
		headers = _get_headers_with_mid(mid)
	http.request_completed.connect(func(result, code, h, body):
		http.queue_free()
		callback.call(result, code, h, body, extra)
	)
	var err = http.request(url, headers, method)
	if err != OK:
		push_error("[BilibiliAPI] HTTP请求失败: %d" % err)
		http.queue_free()
		callback.call(HTTPRequest.RESULT_REQUEST_FAILED, 0, [], PackedByteArray(), extra)


func _request_with_sign(url: String, callback: Callable, extra: Variant = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = _get_headers(), mid: int = 0) -> void:
	var signed_url = await _sign_wbi_url(url)
	_request(signed_url, callback, extra, method, custom_headers, mid)


func _sign_wbi_url(url: String) -> String:
	var key_data = await _get_wbi_key()
	var img_key: String = key_data.get("img_key", "")
	var sub_key: String = key_data.get("sub_key", "")
	if img_key.is_empty() or sub_key.is_empty():
		push_error("[BilibiliAPI] WBI 密钥不完整，无法签名")
		return url
	var signed_url = BilibiliWBI.sign_url(url, img_key, sub_key)
	print("[WBI] 签名后 URL: %s" % signed_url)  # 添加这行
	return signed_url
func _get_wbi_key() -> Dictionary:
	var now = Time.get_unix_time_from_system()
	if now - _wbi_key_cache.get("cached_time", 0) < 1800 and not _wbi_key_cache.get("img_key", "").is_empty():
		return _wbi_key_cache
	
	var urls = [
		"https://api.bilibili.com/x/web-interface/nav",
        "https://api.bilibili.com/x/web-interface/wbi/index"
	]

	for url in urls:
		var http = HTTPRequest.new()
		add_child(http)
		http.request(url, _get_headers(), HTTPClient.METHOD_GET)
		var result: Array = await http.request_completed
		http.queue_free()

		var response_code = result[1]
		var body = result[3] as PackedByteArray
		var body_str = body.get_string_from_utf8()

		if response_code != 200:
			continue

		if body_str.strip_edges().begins_with("<"):
			continue

		var json = JSON.new()
		if json.parse(body_str) != OK:
			continue

		var data_obj = json.get_data()
		if data_obj.get("code") != 0:
			continue

		var data = data_obj.get("data", {})
		var img_url = ""
		var sub_url = ""

		if data.has("wbi_img"):
			img_url = data["wbi_img"].get("img_url", "")
			sub_url = data["wbi_img"].get("sub_url", "")
		else:
			img_url = data.get("img_url", "")
			sub_url = data.get("sub_url", "")

		var img_key = GdScriptFunc.extract_key_from_url(img_url)
		var sub_key = GdScriptFunc.extract_key_from_url(sub_url)

		print("[WBI] 从 %s 提取的 img_key=%s, sub_key=%s" % [url, img_key, sub_key])  # 关键日志

		if not img_key.is_empty() and not sub_key.is_empty():
			_wbi_key_cache = {"img_key": img_key, "sub_key": sub_key, "cached_time": now}
			return _wbi_key_cache

	push_error("[BilibiliAPI] 所有 WBI 接口均失败，无法签名！")
	return _wbi_key_cache

func fetch_user_info_by_mid(mid: String, callback: Callable, max_retries: int = 3) -> void:
	print("[fetch_user_info_by_mid] 开始获取 mid=%s" % mid)
	_fetch_with_retry(mid, callback, max_retries)

func _fetch_with_retry(mid: String, callback: Callable, retries_left: int) -> void:
	var keyword = "uid:" + mid
	var url = "https://api.bilibili.com/x/web-interface/search/type?search_type=bili_user&keyword=%s&page=1&page_size=1&from_source=web_search&platform=pc" % keyword
	
	var headers = _get_headers().duplicate()
	for i in range(headers.size()):
		if headers[i].begins_with("Referer: "):
			headers[i] = "Referer: https://search.bilibili.com"
		elif headers[i].begins_with("Origin: "):
			headers[i] = "Origin: https://search.bilibili.com"
	
	var http = HTTPRequest.new()
	add_child(http)
	http.request(url, headers, HTTPClient.METHOD_GET)
	
	var result = await http.request_completed
	http.queue_free()
	
	var response_code = result[1]
	var body = result[3] as PackedByteArray
	
	if response_code != 200:
		print("[fetch] HTTP 错误: %d" % response_code)
		if retries_left > 0:
			await _wait_and_retry(mid, callback, retries_left - 1)
		else:
			callback.call(null)
		return
	
	var body_str = body.get_string_from_utf8()
	var json = JSON.new()
	if json.parse(body_str) != OK:
		print("[fetch] JSON 解析失败")
		if retries_left > 0:
			await _wait_and_retry(mid, callback, retries_left - 1)
		else:
			callback.call(null)
		return
	
	var data = json.get_data()
	var api_code = data.get("code", -1)
	
	if api_code != 0:
		print("[fetch] API 错误: %d, %s" % [api_code, data.get("message", "")])
		if api_code == -799 and retries_left > 0:
			await _wait_and_retry(mid, callback, retries_left - 1)
		else:
			callback.call(null)
		return
	
	var result_list = data.get("data", {}).get("result", [])
	if result_list.is_empty():
		print("[fetch] 未找到用户")
		callback.call(null)
		return
	
	var user = result_list[0]
	var info = {
		"mid": user.get("mid", 0),
		"name": user.get("uname", ""),
		"face": user.get("upic", "").replace("//", "https://"),
		"sign": user.get("usign", ""),
		"level": user.get("level", 0),
		"fans": user.get("fans", 0),
		"videos": user.get("videos", 0)
	}
	print("[fetch] 成功获取用户名: %s" % info.name)
	callback.call(info)

func _wait_and_retry(mid: String, callback: Callable, retries_left: int) -> void:
	var wait_time = 2 * (4 - retries_left)
	print("[fetch] 等待 %d 秒后重试 (剩余 %d 次)" % [wait_time, retries_left])
	await get_tree().create_timer(wait_time).timeout
	_fetch_with_retry(mid, callback, retries_left)


func fetch_user_videos(mid: String, callback: Callable, page: int = 1, page_size: int = 20, order = "pubdate") -> void:
	var order_str = order
	if order is int:
		order_str = BilibiliConstants.ORDER_MAP.get(order, "pubdate")
	order_str = str(order_str)
	var base = "https://api.bilibili.com/x/space/wbi/arc/search?"
	var query = "pn=" + str(page) + \
				"&ps=" + str(page_size) + \
				"&tid=0&special_type=&order=" + order_str + \
				"&mid=" + mid + \
				"&index=0&keyword=&order_avoided=true&platform=web&web_location=333.1387" + \
				"&dm_img_list=[]" + \
				"&dm_img_str=V2ViR0wgMS4wIChPcGVuR0wgRVMgMi4wIENocm9taXVtKQ" + \
				"&dm_cover_img_str=QU5HTEUgKEFNRCwgQU1EIFJhZGVvbihUTSkgVmVnYSA4IEdyYXBoaWNzICgweDAwMDAxNUQ4KSBEaXJlY3QzRDExIHZzXzVfMCBwc181XzAsIEQzRDExKUdvb2dsZSBJbmMuIChBTU" + \
                "&dm_img_inter=%7B%22ds%22:[],%22wh%22:[3030,2380,102],%22of%22:[205,410,205]%7D"
	var url = base + query
	var headers = _get_headers().duplicate()
	for i in range(headers.size()):
		if headers[i].begins_with("Referer: "):
			headers[i] = "Referer: https://space.bilibili.com"
		elif headers[i].begins_with("Origin: "):
			headers[i] = "Origin: https://space.bilibili.com"
	
	_request_with_sign(url, _on_user_videos_response, [callback], HTTPClient.METHOD_GET, headers)
func _dict_to_query(dict: Dictionary) -> String:
	var parts = []
	for key in dict.keys():
		var value = dict[key]
		if value is String:
			parts.append(key + "=" + value.uri_encode())
		else:
			parts.append(key + "=" + str(value).uri_encode())
	return "&".join(parts)

func _on_user_videos_response(_result, code, _headers, body, extra):
	var callback: Callable = extra[0]
	if code != 200:
		callback.call(null)
		return

	var json = JSON.new()
	var body_str = body.get_string_from_utf8()
	if json.parse(body_str) != OK:
		callback.call(null)
		return

	var data = json.get_data()
	var api_code = data.get("code", -1)
	if api_code != 0:
		print("[fetch_user_videos] API 错误: %d, %s" % [api_code, data.get("message", "")])
		callback.call(null)
		return

	var list_data = data.get("data", {})
	if list_data.is_empty():
		callback.call(null)
		return

	var vlist = list_data.get("list", {}).get("vlist", [])
	var videos = []
	for item in vlist:
		videos.append({
			"link": item.get("bvid", ""),
			"BV": item.get("bvid", ""),
			"title": decode_html_entities(item.get("title", "")),
			"author": item.get("author", ""),
			"play": item.get("play", 0),
			"danmaku": item.get("video_review", 0),
			"duration": item.get("length", ""),
			"description": decode_html_entities(item.get("description", ""))
		})
	callback.call(videos)


func search_bilibili(callback: Callable, keyword: String, num: int = 10, order = 0, page := 1, author: String = "", _tids := 3) -> void:
	if keyword == "bilibili音乐周榜":
		_fetch_music_rank_static(callback)
		return
	var order_str = BilibiliConstants.ORDER_MAP.get(order, "totalrank") if order is int else order
	var query = {"keyword": keyword, "page": page, "order": order_str, "page_size": num, "search_type": "video"}
	var qs = ""
	for k in query:
		if not qs.is_empty(): qs += "&"
		qs += k + "=" + str(query[k]).uri_encode()
	var url = "https://api.bilibili.com/x/web-interface/search/type?" + qs
	url = await _sign_wbi_url(url)
	var headers = _get_headers()
	for i in range(headers.size()):
		if headers[i].begins_with("Referer: "):
			headers[i] = "Referer: https://www.bilibili.com"
			break
	for i in range(headers.size()):
		if headers[i].begins_with("Origin: "):
			headers[i] = "Origin: https://www.bilibili.com"
			break
	_request(url, _on_search_response, [callback, author])

func _on_search_response(_r, code, _h, body, extra):
	var callback: Callable = extra[0]
	var author_filter: String = extra[1] if extra.size() > 1 else ""
	if code != 200:
		push_error("[BilibiliAPI] 搜索请求失败: %d" % code)
		callback.call([{}]); return
	var raw = body.get_string_from_utf8()
	if raw.strip_edges().begins_with("<"):
		push_error("[BilibiliAPI] 搜索被风控拦截，收到HTML"); callback.call([{}]); return
	var json = JSON.new()
	if json.parse(raw) != OK:
		push_error("[BilibiliAPI] JSON解析失败"); callback.call([{}]); return
	var data = json.get_data()
	if data.get("code") != 0:
		push_error("[BilibiliAPI] API错误: %s" % data.get("message")); callback.call([{}]); return
	var videos = []
	for item in data.get("data", {}).get("result", []):
		var bvid = item.get("bvid", "")
		if bvid.is_empty(): continue
		if author_filter != "" and item.get("author", "") != author_filter: continue
		videos.append({
			"link": bvid, "BV": bvid,
			"title": decode_html_entities(item.get("title", "").replace('<em class="keyword">', "").replace("</em>", "")),
			"author": decode_html_entities(item.get("author", "")),
			"play": item.get("play", 0),
			"danmaku": item.get("video_review", 0),
			"duration": item.get("duration", ""),
			"description": decode_html_entities(item.get("description", ""))
		})
	callback.call(videos)

func _fetch_music_rank_static(callback: Callable) -> void:
	_request("https://api.bilibili.com/x/copyright-music-publicity/toplist/all_period?list_type=1", _on_all_period_response, [callback])

func _on_all_period_response(result, code, _h, body, extra):
	var callback: Callable = extra[0]
	if result != HTTPRequest.RESULT_SUCCESS or code != 200:
		push_error("[BilibiliAPI] 获取榜单ID失败"); callback.call([{}]); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK: callback.call([{}]); return
	var data = json.get_data()
	if data.get("code", -1) != 0: callback.call([{}]); return
	var periods = data.get("data", {}).get("list", {})
	var latest_id = 0; var latest_time = 0
	for year in periods:
		for period in periods[year]:
			if period.get("publish_time", 0) > latest_time:
				latest_time = period.get("publish_time", 0)
				latest_id = period.get("ID", 0)
	if latest_id == 0: callback.call([{}]); return
	_fetch_music_list_static(latest_id, callback)

func _fetch_music_list_static(list_id: int, callback: Callable) -> void:
	_request("https://api.bilibili.com/x/copyright-music-publicity/toplist/music_list?list_id=%d" % list_id, _on_music_list_response, [callback])

func _on_music_list_response(_r, code, _h, body, extra):
	var callback: Callable = extra[0]
	if _r != HTTPRequest.RESULT_SUCCESS or code != 200: callback.call([{}]); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK: callback.call([{}]); return
	var data = json.get_data()
	if data.get("code", -1) != 0: callback.call([{}]); return
	var list = data.get("data", {}).get("list", [])
	var videos = []
	for item in list:
		var bvid = item.get("creation_bvid", "")
		if bvid.is_empty(): bvid = item.get("mv_bvid", "")
		if bvid.is_empty(): continue
		videos.append({
			"link": bvid, "BV": bvid,
			"title": decode_html_entities(item.get("creation_title", "")),
			"author": decode_html_entities(item.get("creation_nickname", "")),
			"description": decode_html_entities(item.get("creation_reason", "")),
			"play": item.get("creation_play", 0)
		})
	callback.call(videos)

func fetch_cover(link: String, callback: Callable, width: int = 160, height: int = 160) -> void:
	cover_cache.fetch_cover(link, callback, width, height)

func fetch_video_info(bvid: String, callback: Callable) -> void:
	_request("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid, _on_video_info_response, [bvid, callback])

func _on_video_info_response(_r, code, _h, body, extra):
	var bvid: String = extra[0]
	var callback: Callable = extra[1]
	if code != 200:
		push_error("[BilibiliAPI] 获取视频信息失败 (%s): %d" % [bvid, code])
		callback.call({}); return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("[BilibiliAPI] JSON解析失败 (%s)" % bvid); callback.call({}); return
	var data = json.get_data()
	if data.get("code") != 0:
		push_error("[BilibiliAPI] API错误 (%s): %s" % [bvid, data.get("message")]); callback.call({}); return
	var vd = data.get("data", {})
	if vd.is_empty(): callback.call({}); return
	var own = vd.get("owner", {})
	var stat = vd.get("stat", {})
	var dim = vd.get("dimension", {})
	var pages: Array = vd.get("pages", [])
	var pages_info = []
	for p in pages:
		pages_info.append({
			"cid": p.get("cid", 0), "page": p.get("page", 1), "part": p.get("part", ""),
			"duration": p.get("duration", 0), "dimension": p.get("dimension", {}),
			"first_frame": p.get("first_frame", ""), "vid": p.get("vid", ""), "weblink": p.get("weblink", "")
		})
	var info = {
		"link": vd.get("bvid", bvid), "BV": vd.get("bvid", bvid), "aid": vd.get("aid", 0),
		"title": decode_html_entities(vd.get("title", "")),
		"desc": decode_html_entities(vd.get("desc", "")), "desc_v2": vd.get("desc_v2", []),
		"author": decode_html_entities(own.get("name", "")), "mid": own.get("mid", 0),
		"face": own.get("face", ""), "pic": vd.get("pic", ""),
		"pubdate": vd.get("pubdate", 0), "ctime": vd.get("ctime", 0), "duration": vd.get("duration", 0),
		"cid": vd.get("cid", 0), "videos": vd.get("videos", 1), "copyright": vd.get("copyright", 1),
		"tid": vd.get("tid", 0), "tname": vd.get("tname", ""), "tid_v2": vd.get("tid_v2", 0),
		"tname_v2": vd.get("tname_v2", ""), "dynamic": vd.get("dynamic", ""),
		"dimension": {"width": dim.get("width", 0), "height": dim.get("height", 0), "rotate": dim.get("rotate", 0)},
		"rights": vd.get("rights", {}),
		"stat": {"view": stat.get("view", 0), "danmaku": stat.get("danmaku", 0), "like": stat.get("like", 0),
			"coin": stat.get("coin", 0), "favorite": stat.get("favorite", 0), "share": stat.get("share", 0),
			"reply": stat.get("reply", 0), "now_rank": stat.get("now_rank", 0), "his_rank": stat.get("his_rank", 0),
			"dislike": stat.get("dislike", 0), "evaluation": stat.get("evaluation", "")},
		"subtitle": vd.get("subtitle", {}), "pages": pages_info, "season_id": vd.get("season_id", 0)
	}
	callback.call(info)

func fetch_subtitle_auto(bvid: String, callback: Callable, save_path: String = "") -> void:
	fetch_video_info(bvid, func(info: Dictionary):
		if info.is_empty():
			callback.call({})
			return
		_video_info_cache[bvid] = info
		subtitle_manager.fetch_subtitle_auto(bvid, info, callback, save_path)
	)

func fetch_subtitle_with_info(info: Dictionary, callback: Callable, save_path: String = "") -> void:
	var bvid = info.get("link", "")
	if bvid.is_empty() or info.get("cid", 0) == 0:
		callback.call({})
		return
	_video_info_cache[bvid] = info
	subtitle_manager.fetch_subtitle_auto(bvid, info, callback, save_path)

func _on_subtitle_processed(lrc_path: String, request_id: String) -> void:
	subtitle_manager.handle_correction_result(request_id, lrc_path)

func start_qr_login(login_callback: Callable) -> void:
	on_qr_login_result = login_callback
	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(_on_qr_generated)
	var err = http.request(
		"https://passport.bilibili.com/x/passport-login/web/qrcode/generate",
		PackedStringArray(),
		HTTPClient.METHOD_GET
	)
	if err != OK:
		push_error("[BilibiliAPI] 二维码生成请求失败: %d" % err)
		http.queue_free()

func _on_qr_generated(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	if response_code != 200:
		return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		return
	var data = json.get_data()["data"]
	var url = data["url"]
	var qrcode_key = data["qrcode_key"]
	_display_qrcode(url)
	_poll_login_status(qrcode_key)

func _display_qrcode(content: String) -> void:
	qr_window = preload("res://Scene/Log_in.tscn").instantiate()
	qr_window.close_requested.connect(_on_qr_window_closed)
	add_child(qr_window)
	GdScriptFunc.apply_theme_and_styles_to_node(qr_window)
	var encoded = content.uri_encode()
	var qr_api = "https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=" + encoded
	var img_request = HTTPRequest.new()
	add_child(img_request)
	img_request.request_completed.connect(func(_r, _c, _h, body):
		if not is_instance_valid(qr_window):
			return
		var img = Image.new()
		if img.load_png_from_buffer(body) == OK:
			var tex = ImageTexture.create_from_image(img)
			qr_window.get_node("QRImage").texture = tex
		else:
			push_error("[BilibiliAPI] 二维码图片加载失败")
	)
	img_request.request(qr_api, PackedStringArray(), HTTPClient.METHOD_GET)

func _on_qr_window_closed() -> void:
	if qr_window:
		qr_window.queue_free()
		qr_window = null
	if _poll_timer:
		_poll_timer.stop()
		_poll_timer.queue_free()
		_poll_timer = null
	if _close_delay_timer:
		_close_delay_timer.stop()
		_close_delay_timer.queue_free()
		_close_delay_timer = null
	if on_qr_login_result:
		on_qr_login_result.call(false)

func _close_qr_window() -> void:
	_on_qr_window_closed()

func _poll_login_status(qrcode_key: String) -> void:
	_poll_timer = Timer.new()
	_poll_timer.wait_time = 2.0
	_poll_timer.autostart = true
	_poll_timer.timeout.connect(_check_qr_status.bind(qrcode_key))
	add_child(_poll_timer)

func _check_qr_status(qrcode_key: String) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(func(result, response_code, headers, body):
		if response_code != 200:
			return
		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			return
		var data = json.get_data()["data"]
		var code = data["code"]
		if code == 0:
			if _poll_timer:
				_poll_timer.stop()
			_exchange_cookie(data["url"])
		elif code == 86038:
			if on_qr_login_result:
				on_qr_login_result.call(false)
			_close_qr_window()
	)
	http.request(
		"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + qrcode_key,
		PackedStringArray(),
		HTTPClient.METHOD_GET
	)

func _exchange_cookie(login_url: String) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	http.max_redirects = 0

	var buvid3 = get_or_generate_buvid()
	var cookie_str = "buvid3=" + buvid3 + "; b_nut=" + str(Time.get_unix_time_from_system())
	var headers = PackedStringArray([
		"User-Agent: Mozilla/5.0 ...",
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	http.request_completed.connect(func(result, response_code, resp_headers, body):
		for header in resp_headers:
			if header.begins_with("Set-Cookie: "):
				var cookie_part = header.trim_prefix("Set-Cookie: ")
				var parts = cookie_part.split(";")
				if parts.size() > 0:
					var kv = parts[0].strip_edges()
					var eq_pos = kv.find("=")
					if eq_pos != -1:
						var key = kv.substr(0, eq_pos)
						var value = kv.substr(eq_pos + 1)
						match key:
							"SESSDATA":
								GdScriptFunc.set_data("AccountData", "SESSDATA", value)
							"bili_jct":
								GdScriptFunc.set_data("AccountData", "bili_jct", value)
							"DedeUserID":
								GdScriptFunc.set_data("AccountData", "DedeUserID", value)
							"DedeUserID__ckMd5":
								GdScriptFunc.set_data("AccountData", "DedeUserID__ckMd5", value)
							"sid":
								GdScriptFunc.set_data("AccountData", "sid", value)
							"bili_ticket":
								GdScriptFunc.set_data("AccountData", "bili_ticket", value)
							"bili_ticket_expires":
								GdScriptFunc.set_data("AccountData", "bili_ticket_expires", value)
		_load_avatar_and_delayed_close()
	)
	var err = http.request(login_url, headers, HTTPClient.METHOD_GET)
	if err != OK:
		push_error("[BilibiliAPI] 请求失败: %d" % err)
		_close_qr_window()
		if on_qr_login_result:
			on_qr_login_result.call(false)

func _load_avatar_and_delayed_close() -> void:
	fetch_user_avatar(func(texture: ImageTexture):
		if is_instance_valid(qr_window) and texture != null:
			qr_window.get_node("QRImage").texture = texture
		_start_delayed_close()
	)

func _start_delayed_close() -> void:
	if not is_instance_valid(qr_window):
		return
	_close_delay_timer = Timer.new()
	_close_delay_timer.wait_time = 0.5
	_close_delay_timer.one_shot = true
	_close_delay_timer.timeout.connect(_on_delayed_close_timeout)
	add_child(_close_delay_timer)
	_close_delay_timer.start()

func _on_delayed_close_timeout() -> void:
	if on_qr_login_result:
		on_qr_login_result.call(true)
	_close_qr_window()

func fetch_user_avatar(callback: Callable) -> void:
	var sessdata = GdScriptFunc.get_data("AccountData", "SESSDATA")
	if sessdata == null or sessdata == "":
		callback.call(null)
		return

	var cookie_str = "SESSDATA=" + sessdata
	var bili_jct = GdScriptFunc.get_data("AccountData", "bili_jct")
	if bili_jct != null:
		cookie_str += "; bili_jct=" + bili_jct
	var dedeuserid = GdScriptFunc.get_data("AccountData", "DedeUserID")
	if dedeuserid != null:
		cookie_str += "; DedeUserID=" + dedeuserid

	var nav_headers = PackedStringArray([
		"User-Agent: Mozilla/5.0 ...",
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	var http_nav = HTTPRequest.new()
	add_child(http_nav)
	http_nav.request_completed.connect(func(result, response_code, headers, body):
		http_nav.queue_free()
		if response_code != 200:
			callback.call(null); return
		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			callback.call(null); return
		var face_url = json.get_data().get("data", {}).get("face", "")
		if face_url == "":
			callback.call(null); return
		var img_request = HTTPRequest.new()
		add_child(img_request)
		img_request.request_completed.connect(func(_r, _c, _h, img_body):
			img_request.queue_free()
			var img = Image.new()
			if img.load_jpg_from_buffer(img_body) == OK or img.load_png_from_buffer(img_body) == OK:
				callback.call(ImageTexture.create_from_image(img))
			else:
				callback.call(null)
		)
		img_request.request(face_url, PackedStringArray(), HTTPClient.METHOD_GET)
	)
	http_nav.request("https://api.bilibili.com/x/web-interface/nav", nav_headers, HTTPClient.METHOD_GET)

func get_csrf() -> String:
	return GdScriptFunc.get_data("AccountData", "bili_jct", "")

static func get_or_generate_buvid() -> String:
	if not _cached_buvid.is_empty():
		return _cached_buvid
	_cached_buvid = GdScriptFunc.get_data("Network", "buvid3", "")
	if _cached_buvid != "":
		return _cached_buvid
	_cached_buvid = generate_fingerprint_buvid()
	GdScriptFunc.set_data("Network", "buvid3", _cached_buvid)
	return _cached_buvid

static func generate_fingerprint_buvid() -> String:
	var sz = DisplayServer.screen_get_size()
	var info = [
		OS.get_name(),
		str(OS.get_processor_count()),
		str(sz.x),
		str(sz.y),
		OS.get_locale(),
		"GodotEngine/" + Engine.get_version_info().string,
		DisplayServer.get_name()
	]
	var h = "||".join(info).md5_text().to_upper()
	return h.substr(0, 8) + "-" + h.substr(8, 4) + "-" + h.substr(12, 4) + "-" + h.substr(16, 4) + "-" + h.substr(20, 12) + "infoc"

static func decode_html_entities(text: String) -> String:
	return BilibiliHTMLDecoder.decode(text)

static func generate_fake_b_nut() -> String:
	return str(Time.get_unix_time_from_system())

static func _random_string(length: int = 16) -> String:
	const CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	var res = ""
	for i in range(length):
		res += CHARS[randi() % CHARS.length()]
	return res

static func _get_or_generate_cookie_field(key: String, generator: Callable) -> String:
	var val = GdScriptFunc.get_data("Network", key, "")
	if val.is_empty():
		val = generator.call()
		GdScriptFunc.set_data("Network", key, val)
	return val

static func _generate_buvid4() -> String:
	var uuid = "%04x%04x-%04x-%04x-%04x-%04x%04x%04x" % [randi() % 0xFFFF, randi() % 0xFFFF, randi() % 0xFFFF, (randi() % 0xFFFF) | 0x4000, (randi() % 0xFFFF) | 0x8000, randi() % 0xFFFF, randi() % 0xFFFF, randi() % 0xFFFF]
	return uuid + "-" + str(Time.get_unix_time_from_system()) + "-" + _random_string(20)

static func _generate_fingerprint() -> String:
	return _random_string(32).md5_text()

static func _generate_rpdid() -> String:
	return _random_string(30)

static func _generate_b_lsid() -> String:
	return _random_string(8).to_upper() + "_" + _random_string(12).to_upper()
