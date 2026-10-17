[out:json][timeout:900];
area["ISO3166-1"="DE"]["admin_level"="2"]["boundary"="administrative"]->.germany;
rel(area.germany)["boundary"="administrative"]["admin_level"="4"]["ISO3166-2"~"^DE-[A-Z]{2}$"];
out geom;
