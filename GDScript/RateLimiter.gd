extends Node
## 全局 B 站请求限速器。

var _last_request_time: int = 0
var _min_gap_ms: int = 250
var _mutex := Mutex.new()

## 请求前调用；无返回值，但会 await 到轮次可用。
func wait_turn() -> void:
	_mutex.lock()
	var now: int = Time.get_ticks_msec()
	var wait: int = maxi(0, _min_gap_ms - (now - _last_request_time))
	_last_request_time = now + wait
	_mutex.unlock()
	if wait > 0:
		await get_tree().create_timer(wait / 1000.0).timeout
