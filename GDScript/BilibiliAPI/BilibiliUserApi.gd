class_name BilibiliUserApi
extends RefCounted

var _http: BilibiliHttpClient

func _init(http: BilibiliHttpClient) -> void:
	_http = http

# ---------------- 用户信息 ----------------

func fetch_by_mid(mid: String, callback: Callable, max_retries: int = 3) -> void:
	_fetch_with_retry(mid, callback, max_retries)

func _fetch_with_retry(mid: String, callback: Callable, retries_left: int) -> void:
	
	var keyword = "uid:" + mid
	var url = "https://api.bilibili.com/x/web-interface/search/type?search_type=bili_user&keyword=%s&page=1&page_size=1&from_source=web_search&platform=pc" % keyword

	var headers = _http.with_origin(_http.get_headers(), "https://search.bilibili.com", "https://search.bilibili.com")
	await RateLimiter.wait_turn()

	var http = HTTPRequest.new()
	_http.host.add_child(http)
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
	await RateLimiter.wait_turn()
	_fetch_with_retry(mid, callback, retries_left)

# ---------------- 用户视频列表（主接口） ----------------

func fetch_videos(mid: String, callback: Callable, page: int = 1, page_size: int = 20, order = "pubdate") -> void:
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
	var headers = _http.with_origin(_http.get_headers(), "https://space.bilibili.com", "https://space.bilibili.com")
	_http.request_with_sign(url, _on_user_videos_response, [callback], HTTPClient.METHOD_GET, headers)

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
			"title": BilibiliHTMLDecoder.decode(item.get("title", "")),
			"author": item.get("author", ""),
			"play": item.get("play", 0),
			"danmaku": item.get("video_review", 0),
			"duration": item.get("length", ""),
			"description": BilibiliHTMLDecoder.decode(item.get("description", ""))
		})
	callback.call(videos)

# ---------------- 备用方案：medialist ----------------

func fetch_videos_medialist(mid: String, username: String, callback: Callable, page: int = 1, page_size: int = 20) -> void:
	var bvid = await _search_one_video_bvid(username)
	if bvid.is_empty():
		callback.call([])
		return

	var aid = BilibiliBvCodec.bv_to_aid(bvid)
	if aid == 0:
		callback.call([])
		return

	var base = "https://api.bilibili.com/x/v2/medialist/resource/list"
	var query_params = {
		"out_referer": "https://space.bilibili.com/%s/upload/video" % mid,
		"mobi_app": "web",
		"type": "1",
		"biz_id": mid,
		"ps": str(page_size),
		"desc": "true",
		"sort_field": "1",
		"tid": "0",
		"bvid": "",
		"oid": str(aid),
		"otype": "2",
		"with_current": "false",
		"direction": "false",
		"preview": "0",
		"use_pn": "false",
		"pn": str(page)
	}
	var qs = ""
	for key in query_params:
		if not qs.is_empty(): qs += "&"
		qs += key + "=" + query_params[key].uri_encode()
	var url = base + "?" + qs

	var res = await _http.request_async(url)
	var code = res[1]
	var body = res[3] as PackedByteArray
	if code != 200:
		callback.call([])
		return

	var json = JSON.new()
	var body_str = body.get_string_from_utf8()
	if body_str.strip_edges().begins_with("<") or json.parse(body_str) != OK:
		callback.call([])
		return

	var data = json.get_data()
	if data.get("code") != 0:
		callback.call([])
		return

	var media_list = data.get("data", {}).get("media_list")
	if not media_list is Array:
		media_list = []

	var videos = []
	for item in media_list:
		var bvid_item = item.get("bv_id", "")
		if bvid_item.is_empty(): continue
		videos.append({
			"link": bvid_item,
			"BV": bvid_item,
			"title": BilibiliHTMLDecoder.decode(item.get("title", "")),
			"author": item.get("upper", {}).get("name", username),
			"play": 0,
			"danmaku": 0,
			"duration": BilibiliBvCodec.format_duration(item.get("duration", 0)),
			"description": BilibiliHTMLDecoder.decode(item.get("intro", ""))
		})
	callback.call(videos)

func _search_one_video_bvid(username: String) -> String:
	var keyword_encoded = username.uri_encode()
	var url = "https://api.bilibili.com/x/web-interface/search/type?search_type=video&keyword=%s&page=1&page_size=1" % keyword_encoded
	var headers = _http.with_origin(_http.get_headers(), "https://search.bilibili.com", "https://search.bilibili.com")

	var res = await _http.request_async(url, HTTPClient.METHOD_GET, headers)
	var code = res[1]
	var body = res[3] as PackedByteArray
	if code != 200:
		return ""
	var json = JSON.new()
	var body_str = body.get_string_from_utf8()
	if body_str.strip_edges().begins_with("<"):
		return ""
	if json.parse(body_str) != OK:
		return ""
	var data = json.get_data()
	if data.get("code") != 0:
		return ""
	var result_list = data.get("data", {}).get("result", [])
	if result_list.is_empty():
		return ""
	return result_list[0].get("bvid", "")

## 获取当前登录用户创建的所有收藏夹
## callback 参数: Array[Dictionary]，每项含 id/fid/title/media_count/attr
func fetch_fav_folders(callback: Callable) -> void:
	await RateLimiter.wait_turn()
	var mid: String = str(GdScriptFunc.get_data("AccountData", "DedeUserID", ""))
	if mid.is_empty():
		push_error("[BilibiliUserApi] 未登录，无法获取收藏夹")
		callback.call([])
		return

	var url = "https://api.bilibili.com/x/v3/fav/folder/created/list-all?up_mid=" + mid
	var headers = _http.with_origin(
		_http.get_headers(),
		"https://space.bilibili.com/%s/favlist" % mid,
        "https://space.bilibili.com"
	)
	_http.request_with_sign(url, _on_fav_folders_received, [callback], HTTPClient.METHOD_GET, headers)


func _on_fav_folders_received(_result: int, code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var callback: Callable = extra[0]
	if code != 200:
		push_error("[BilibiliUserApi] 获取收藏夹列表失败，HTTP %d" % code)
		callback.call([])
		return

	var json = JSON.new()
	var body_str = body.get_string_from_utf8()
	if json.parse(body_str) != OK:
		push_error("[BilibiliUserApi] 收藏夹列表 JSON 解析失败")
		callback.call([])
		return

	var data = json.get_data()
	if data.get("code", -1) != 0:
		push_error("[BilibiliUserApi] 收藏夹列表 API 错误: %s" % data.get("message", ""))
		callback.call([])
		return

	var list: Array = data.get("data", {}).get("list", [])
	var result: Array[Dictionary] = []
	for item in list:
		result.append({
			"id": int(item.get("id", 0)),
			"fid": int(item.get("fid", 0)),
			"title": BilibiliHTMLDecoder.decode(item.get("title", "")),
			"media_count": int(item.get("media_count", 0)),
			"attr": int(item.get("attr", 0)),
		})

	print("[BilibiliUserApi] 获取到 %d 个收藏夹" % result.size())
	callback.call(result)


## 获取指定收藏夹的内容（单页）
## media_id: 收藏夹 mlid
## pn: 页码（从 1 开始）
## ps: 每页数量（1–20）
## callback 参数: Dictionary { medias: Array, has_more: bool, total: int }
func fetch_fav_items(media_id: int, pn: int = 1, ps: int = 20, callback: Callable = Callable()) -> void:
	await RateLimiter.wait_turn()
	if ps < 1 or ps > 20:
		ps = 20
	if pn < 1:
		pn = 1

	var url = ("https://api.bilibili.com/x/v3/fav/resource/list"
			 + "?media_id=%d&pn=%d&ps=%d&platform=web&order=mtime" % [media_id, pn, ps])
	var headers = _http.with_origin(
		_http.get_headers(),
		"https://www.bilibili.com",
        "https://www.bilibili.com"
	)
	_http.request_with_sign(url, _on_fav_items_received, [callback], HTTPClient.METHOD_GET, headers)


func _on_fav_items_received(_result: int, code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var callback: Callable = extra[0]
	if code != 200:
		push_error("[BilibiliUserApi] 获取收藏夹内容失败，HTTP %d" % code)
		callback.call({"medias": [], "has_more": false, "total": 0})
		return

	var json = JSON.new()
	var body_str = body.get_string_from_utf8()
	if json.parse(body_str) != OK:
		push_error("[BilibiliUserApi] 收藏夹内容 JSON 解析失败")
		callback.call({"medias": [], "has_more": false, "total": 0})
		return

	var data = json.get_data()
	if data.get("code", -1) != 0:
		push_error("[BilibiliUserApi] 收藏夹内容 API 错误: %s" % data.get("message", ""))
		callback.call({"medias": [], "has_more": false, "total": 0})
		return

	var data_obj = data.get("data", {})
	var medias: Array = data_obj.get("medias", [])
	var has_more: bool = data_obj.get("has_more", false)
	var total: int = data_obj.get("info", {}).get("media_count", 0)

	var result: Array[Dictionary] = []
	for m in medias:
		if m == null:
			continue
		var upper: Dictionary = m.get("upper", {})
		var cnt: Dictionary = m.get("cnt_info", {})
		result.append({
			"link": m.get("bvid", ""),
			"BV": m.get("bvid", ""),
			"title": BilibiliHTMLDecoder.decode(m.get("title", "")),
			"author": BilibiliHTMLDecoder.decode(upper.get("name", "")),
			"description": BilibiliHTMLDecoder.decode(m.get("intro", "")),
			"cover": m.get("cover", ""),
			"duration": int(m.get("duration", 0)),
			"play": int(cnt.get("play", 0)),
			"is_network": true,
		})

	callback.call({
		"medias": result,
		"has_more": has_more,
		"total": total,
	})
## 递归获取一个收藏夹的全部内容（自动翻页）
## callback 参数: Array[Dictionary]，所有视频条目
func fetch_all_fav_items(media_id: int, callback: Callable) -> void:
	var all_items: Array[Dictionary] = []
	_fetch_fav_page_recursive(media_id, 1, all_items, callback)


func _fetch_fav_page_recursive(media_id: int, pn: int, accumulated: Array[Dictionary], callback: Callable) -> void:
	fetch_fav_items(media_id, pn, 20, func(page_data: Dictionary):
		var medias: Array = page_data.get("medias", [])
		for item in medias:
			accumulated.append(item)

		if page_data.get("has_more", false) and not medias.is_empty():
			_fetch_fav_page_recursive(media_id, pn + 1, accumulated, callback)
		else:
			print("[BilibiliUserApi] 收藏夹 %d 全部获取完成，共 %d 条" % [media_id, accumulated.size()])
			callback.call(accumulated)
	)
