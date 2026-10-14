[out:json][timeout:300];
area["ISO3166-1"="AT"]["admin_level"="2"]["boundary"="administrative"]->.austria;
rel(area.austria)["boundary"="administrative"]["admin_level"="4"]["ISO3166-2"~"^AT-[1-9]$"];
out geom;
