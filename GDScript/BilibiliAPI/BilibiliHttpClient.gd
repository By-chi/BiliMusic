class_name BilibiliHttpClient
extends RefCounted

# 用于 add_child(HTTPRequest) 的宿主 Node（就是 autoload 本体）
var host: Node

var _wbi_key_cache := {"img_key": "", "sub_key": "", "cached_time": 0}

func _init(p_host: Node) -> void:
	host = p_host

# ---------------- Headers ----------------

func get_image_headers() -> PackedStringArray:
	return PackedStringArray([
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: https://www.bilibili.com"
	])

func get_headers() -> PackedStringArray:
	return get_headers_with_mid(0)

func get_headers_with_mid(mid: int = 0) -> PackedStringArray:
	# buvid4 优先用官方指纹，缺失时才本地生成
	var buvid4 := BilibiliCookieStore.get_cached_buvid4()
	if buvid4.is_empty():
		buvid4 = BilibiliCookieStore.get_or_generate_cookie_field(
			"buvid4", Callable(BilibiliCookieStore, "generate_buvid4")
		)

	var cookies = [
		"buvid3=" + BilibiliCookieStore.get_or_generate_buvid(),
		"buvid4=" + buvid4,
		"b_nut=" + BilibiliCookieStore.generate_fake_b_nut(),
		"rpdid=" + BilibiliCookieStore.get_or_generate_cookie_field("rpdid", Callable(BilibiliCookieStore, "generate_rpdid")),
		"_uuid=" + BilibiliCookieStore.get_or_generate_cookie_field("_uuid", Callable(BilibiliCookieStore, "generate_uuid")),
		"theme-tip-show=SHOWED",
		"theme-avatar-tip-show=SHOWED",
		"theme-switch-show=SHOWED",
		"theme_style=dark",
		"hit-dyn-v2=1",
		"buvid_fp_plain=undefined",
		"LIVE_BUVID=AUTO" + str(Time.get_unix_time_from_system()) + "411",
		"fingerprint=" + BilibiliCookieStore.get_or_generate_cookie_field("fingerprint", Callable(BilibiliCookieStore, "generate_fingerprint")),
		"buvid_fp=" + BilibiliCookieStore.get_or_generate_cookie_field("buvid_fp", Callable(BilibiliCookieStore, "generate_fingerprint")),
		"PVID=1",
		"ogv_device_support_dolby=0",
		"ogv_device_support_hdr=0",
		"browser_resolution=" + str(DisplayServer.screen_get_size().x) + "-" + str(DisplayServer.screen_get_size().y),
		"home_feed_column=4",
		"b_lsid=" + BilibiliCookieStore.get_or_generate_cookie_field("b_lsid", Callable(BilibiliCookieStore, "generate_b_lsid")),
		"CURRENT_FNVAL=4048",
		"CURRENT_QUALITY=0",
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
	if bp != "": cookies.append("bp_t_offset_" + uid + "=" + bp)

	var cookie = "; ".join(cookies) + ";"
	var referer = "https://space.bilibili.com/"
	if mid != 0:
		referer += str(mid) + "/upload/video"

	return PackedStringArray([
		"User-Agent: " + BilibiliCookieStore.get_dynamic_user_agent(),
		"Referer: " + referer,
		"Origin: https://space.bilibili.com",
		"Accept: */*",
		"Accept-Language: zh-CN,zh-Hans;q=0.9",
		"Accept-Encoding: gzip, deflate, br",
		"Cache-Control: no-cache",
		"Pragma: no-cache",
		'Sec-Ch-Ua: "Not;A=Brand";v="8", "Chromium";v="120", "Microsoft Edge";v="120"',
		"Sec-Ch-Ua-Mobile: ?0",
		'Sec-Ch-Ua-Platform: "macOS"',
		"Sec-Fetch-Dest: empty",
		"Sec-Fetch-Mode: cors",
		"Sec-Fetch-Site: same-site",
		"Dnt: 1",
		"Priority: u=1, i",
		"Cookie: " + cookie
	])

# 便捷工具：替换 headers 里的 Referer / Origin
func with_origin(headers: PackedStringArray, referer: String, origin: String) -> PackedStringArray:
	var h = headers.duplicate()
	for i in h.size():
		if h[i].begins_with("Referer: "):
			h[i] = "Referer: " + referer
		elif h[i].begins_with("Origin: "):
			h[i] = "Origin: " + origin
	return h

# ---------------- HTTP ----------------

func request(url: String, callback: Callable, extra = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = PackedStringArray(), mid: int = 0) -> void:
	var http = HTTPRequest.new()
	host.add_child(http)
	var headers := custom_headers
	if headers.is_empty():
		headers = get_headers_with_mid(mid)
	http.request_completed.connect(func(result, code, h, body):
		http.queue_free()
		callback.call(result, code, h, body, extra)
	)
	var err = http.request(url, headers, method)
	if err != OK:
		push_error("[BilibiliAPI] HTTP请求失败: %d" % err)
		http.queue_free()
		callback.call(HTTPRequest.RESULT_REQUEST_FAILED, 0, PackedStringArray(), PackedByteArray(), extra)

func request_with_sign(url: String, callback: Callable, extra = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = PackedStringArray(), mid: int = 0) -> void:
	var signed_url = await sign_wbi_url(url)
	request(signed_url, callback, extra, method, custom_headers, mid)

func request_async(url: String, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = PackedStringArray()) -> Array:
	var http = HTTPRequest.new()
	host.add_child(http)
	var headers := custom_headers
	if headers.is_empty():
		headers = get_headers()
	http.request(url, headers, method)
	var result = await http.request_completed
	http.queue_free()
	return result

# ---------------- WBI ----------------

func sign_wbi_url(url: String) -> String:
	var key_data = await get_wbi_key()
	var img_key: String = key_data.get("img_key", "")
	var sub_key: String = key_data.get("sub_key", "")
	if img_key.is_empty() or sub_key.is_empty():
		return url
	return BilibiliWBI.sign_url(url, img_key, sub_key)

func get_wbi_key() -> Dictionary:
	var now = Time.get_unix_time_from_system()
	if now - _wbi_key_cache.get("cached_time", 0) < 1800 and not _wbi_key_cache.get("img_key", "").is_empty():
		return _wbi_key_cache

	var urls = [
		"https://api.bilibili.com/x/web-interface/nav",
		"https://api.bilibili.com/x/web-interface/wbi/index"
	]
	for url in urls:
		var http = HTTPRequest.new()
		host.add_child(http)
		http.request(url, get_headers(), HTTPClient.METHOD_GET)
		var result: Array = await http.request_completed
		http.queue_free()

		var response_code = result[1]
		var body = result[3] as PackedByteArray
		var body_str = body.get_string_from_utf8()
		if response_code != 200: continue
		if body_str.strip_edges().begins_with("<"): continue

		var json = JSON.new()
		if json.parse(body_str) != OK: continue
		var data_obj = json.get_data()
		if data_obj.get("code") != 0: continue

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
		print("[WBI] 从 %s 提取的 img_key=%s, sub_key=%s" % [url, img_key, sub_key])
		if not img_key.is_empty() and not sub_key.is_empty():
			_wbi_key_cache = {"img_key": img_key, "sub_key": sub_key, "cached_time": now}
			return _wbi_key_cache
	return _wbi_key_cache
