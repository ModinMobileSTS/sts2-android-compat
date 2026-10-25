extends SpineSprite

func _enter_tree():
	Engine.set_meta("spine_test_sprite_entries", Engine.get_meta("spine_test_sprite_entries", 0) + 1)
