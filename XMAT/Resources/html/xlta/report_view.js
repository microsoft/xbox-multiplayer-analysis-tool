ruleDisplayFunctions = [];

ruleDisplayFunctions["default"] = function(rule){
    var resultTable = $("<table>").addClass("detail-table");
	var headerRow = $("<tr>");
	var resultRow = $("<tr>");
	
	for(elem in rule.ResultData)
	{
		var headerCell = $("<td>").addClass("header").text(elem);
		headerRow.append(headerCell);
		
		var resultCell = $("<td>").addClass("cell").text(rule.ResultData[elem]);
		resultRow.append(resultCell);
	}
	
	resultTable.append(headerRow, resultRow);
	
	return resultTable;
};

ruleDisplayFunctions["Repeated Calls"] = function(rule){
	var results = rule.ResultData;
	
	if(results["Duplicates"] == 0)
	{
		return $("<div>").text("No duplicated calls found.");
	}

	var resultTable = $("<table>").addClass("detail-table");
	var headerRow = $("<tr>");
	var resultRow = $("<tr>");
	
	headerRow.append($("<td>").addClass("header").text("Total Calls"));
	headerRow.append($("<td>").addClass("header").text("Duplicates"));
	headerRow.append($("<td>").addClass("header").text("Percentage"));
	
	resultRow.append($("<td>").addClass("cell").text(results["Total Calls"]));
	resultRow.append($("<td>").addClass("warning").text(results["Duplicates"]));
	resultRow.append($("<td>").addClass("cell").text((Number(results["Percentage"]) * 100).toFixed(2) + "%"));
	
	resultTable.append(headerRow, resultRow);
	
	return resultTable;
};

ruleDisplayFunctions["Throttled Call Detection"] = function(rule){
	var graphLocation = $("<div>").css({"width": "700px", "height": "250px", "float": "center", "padding": "10px"});
	var results = rule.ResultData;
	
	if(results["Throttled Calls"] == 0)
	{
		return $("<div>").text("No throttled calls found.");
	}
	
	var resultTable = $("<table>").addClass("detail-table");
	var headerRow = $("<tr>");
	var resultRow = $("<tr>");
	
	headerRow.append($("<td>").addClass("header").text("Total Calls"));
	headerRow.append($("<td>").addClass("header").text("Throttled Calls"));
	headerRow.append($("<td>").addClass("header").text("Percentage"));
	
	resultRow.append($("<td>").addClass("cell").text(results["Total Calls"]));
	if(rule.Result == "Warning")
	{
		resultRow.append($("<td>").addClass("warning").text(results["Throttled Calls"]).css("background-color", "yellow"));
	}
	else if(rule.Result == "Error")
	{
		resultRow.append($("<td>").addClass("error").text(results["Throttled Calls"]).css("background-color", "red"));
	}
	resultRow.append($("<td>").addClass("cell").text((Number(results["Percentage"]) * 100).toFixed(2) + "%"));
	
	resultTable.append(headerRow, resultRow);
	
	return resultTable;
};

ruleDisplayFunctions["Polling Detection"] = function(rule){
	var count = rule.ResultData["Polling Sequences Found"];
	if(count == 0)
	{
		return $("<div>").text("No polling sequences found.");
	}
	return $("<div>").html("<b>Polling Sequences Found:</b> " + rule.ResultData["Polling Sequences Found"]);
}

ruleDisplayFunctions["Call Frequency"] = function(rule){
	var sustainedExceeded = rule.ResultData["Times Sustained Exceeded"];
	var burstExceeded = rule.ResultData["Times Sustained Exceeded"];
	
	if(sustainedExceeded == 0 && burstExceeded == 0)
	{
		return $("<div>").text("All calls within allowed limits.");
	}
	
	var result = $("<table>").attr("align","center").addClass("detail-table");
	
	result.append($("<tr>").append($("<td>").addClass("header"), $("<td>").addClass("header").text("Sustained"), $("<td>").addClass("header").text("Burst")));
	result.append($("<tr>").append($("<td>").addClass("header").text("Call Period in Seconds"), $("<td>").addClass("cell").text(rule.ResultData["Sustained Call Period"]), $("<td>").addClass("cell").text(rule.ResultData["Burst Call Period"])));
	result.append($("<tr>").append($("<td>").addClass("header").text("Max Calls in Period"),    $("<td>").addClass("cell").text(rule.ResultData["Sustained Call Limit"]),  $("<td>").addClass("cell").text(rule.ResultData["Burst Call Limit"])));

	if(Number(sustainedExceeded) > 0)
	{
	    sustainedExceeded = $("<td>").addClass("error").text(rule.ResultData["Times Sustained Exceeded"]);
	}
	else
	{
	    sustainedExceeded = $("<td>").addClass("cell").text(rule.ResultData["Times Sustained Exceeded"]);
	}
	
	if(Number(burstExceeded) > 0)
	{
	    burstExceeded = $("<td>").addClass("error").text(rule.ResultData["Times Burst Exceeded"]);
	}
	else
	{
	    burstExceeded = $("<td>").addClass("cell").text(rule.ResultData["Times Burst Exceeded"]);
	}
	
	result.append($("<tr>").append($("<td>").addClass("header").text("Times Exceeded"), sustainedExceeded, burstExceeded));
	
	return result;
}

ruleDisplayFunctions["Burst Detection"] = function(rule){
	
	if(rule.ResultData["Total Bursts"] == 0)
	{
		return $("<div>").text("No bursts of calls detected.");
	}
	
	var resultTable = $("<table>").addClass("detail-table");
	var headerRow = $("<tr>");
	var resultRow = $("<tr>");
	
	headerRow.append($("<td>").addClass("header").text("Average Calls Per Second"));
	headerRow.append($("<td>").addClass("header").text("Std. Deviation"));
	headerRow.append($("<td>").addClass("header").text("Min. Calls in Burst"));
	headerRow.append($("<td>").addClass("header").text("Burst Time Window in Seconds"));
	headerRow.append($("<td>").addClass("header").text("Total Bursts Found"));
	
	resultRow.append($("<td>").addClass("cell").text(Number(rule.ResultData["Avg. Calls Per Sec."]).toFixed(3)));
	resultRow.append($("<td>").addClass("cell").text(Number(rule.ResultData["Std. Deviation"]).toFixed(4)));
	resultRow.append($("<td>").addClass("cell").text(rule.ResultData["Burst Size"]));
	resultRow.append($("<td>").addClass("cell").text(rule.ResultData["Burst Window"]));
	
	var totalBursts = $("<td>").addClass("warning").text(rule.ResultData["Total Bursts"]);
	
	resultRow.append(totalBursts);
	
	resultTable.append(headerRow, resultRow);
	
	return resultTable;
};

ruleDisplayFunctions["Small-Batch Detection"] = function(rule){
	if(rule.ResultData["Calls Below Count"] == 0)
	{
		return $("<div>").text("No calls with small batch counts detected.");
	}
	var resultTable = $("<table>").addClass("detail-table");
	var headerRow = $("<tr>");
	var resultRow = $("<tr>");
	
	headerRow.append($("<td>").addClass("header").text("Total Batch Calls"));
	headerRow.append($("<td>").addClass("header").text("Min. Users Allowed"));
	headerRow.append($("<td>").addClass("header").text("Calls Below Count"));
	headerRow.append($("<td>").addClass("header").text("Percent Below Count"));
	
	resultRow.append($("<td>").addClass("cell").text(Number(rule.ResultData["Total Batch Calls"])));
	resultRow.append($("<td>").addClass("cell").text(Number(rule.ResultData["Min. Users Allowed"])));
	resultRow.append($("<td>").addClass("warning").text(rule.ResultData["Calls Below Count"]));
	resultRow.append($("<td>").addClass("cell").text((Number(rule.ResultData["% Below Count"]).toFixed(4) * 100) + "%"));
	
	resultTable.append(headerRow, resultRow);
	
	return resultTable;
};

ruleDisplayFunctions["Batch Frequency"] = function(rule){
	if(rule.ResultData["Times Exceeded"] == 0)
	{
		return $("<div>").text("No batch calls to potentially combine.");
	}
	
	var table = ruleDisplayFunctions["default"](rule);
	$($($(table.children()[0]).children()[1]).children()[2]).addClass("warning");
	
	return table;
};

toggleChildren = function(elem){
	var e = $(elem);
	e.children("div:last-child").slideToggle(100);
	var header = e.children("h4");
	var headerChild = header.children("a");
	if(headerChild.children().text() == "-"){
		headerChild.html("<div style=\"width: 15px; float: left\">+</div>");
	}
	else {
		headerChild.html("<div style=\"width: 15px; float: left\">-</div>");
	}
};

showChildren = function(elem){
	var e = $(elem);
	e.children("div:last-child").show();
	var header = e.children("h4");
	var headerChild = header.children("a");
	headerChild.html("<div style=\"width: 15px; float: left\">-</div>");
};

toggleNext = function(elem){
	var e = $(elem);
	e.next().slideToggle(100);
	var expander = e.children("a");
	if(expander.children().text() == "-"){
		expander.html("<div style=\"width: 15px; float: left\">+</div>");
	}
	else {
		expander.html("<div style=\"width: 15px; float: left\">-</div>");
	}
};

toggleId = function(id){
	var location = $('#' + id);
	toggleChildren(location);
}

jumpToId = function(id) {
	var location = $('#' + id);
	console.log(location);
	showChildren(location);
	$(window).scrollTop(location.offset().top - 120);
};

jumpToCall = function(id) {
	var location = $('#' + id);
	$(window).scrollTop(location.offset().top - 120);
};

jumpToTop = function() {
	$(window).scrollTop(0);
}

API = "Uri";
var rules = ["Call Frequency", "Burst Detection", "Throttled Call Detection", "Small-Batch Detection", "Batch Frequency", "Repeated Calls", "Polling Detection"];

function StatsPage() {
	this.timelineGraphData = [];
	this.timelineGraphOptions = {
			lines: {
				show: true
			},
			zoom: {
				interactive: true
			},
			pan: {
				interactive: true
			},
			xaxis: {
				min: 0,
				tickDecimals: 0,
				tickFormatter: function(number, obj) {
					var seconds = number % 60;
					var minutes = (number / 60).toFixed(0);
					
					if(seconds < 10) {
						return (minutes + ":0" + seconds);
					}
					else {
						return (minutes + ":" + seconds);
					}
				}
				
			},
			yaxis: {
				min: 0,
				tickDecimals: 0,
				minTickSize: 1,
			},
			grid: {
				backgroundColor: "#3C3C3C",
				borderColor: "black",
				borderWidth: 1
			}
		};
		
	this.callCountGraphData = [];
	this.callCountGraphOptions = {
			series: {
				bars: {
						show: true,
						barWidth: .6,
						align: "center",
						horizontal: true,
					lineWidth: 1
				},
				
			},
			colors: ["#00BB00"],
			yaxis: {
				mode: "categories",
				tickLength: 0,
				
			},
			grid: {
				backgroundColor: "#3C3C3C",
				borderColor: "black",
				borderWidth: 1,
			}
		};
}

StatsPage.prototype = {
	build: function(stats, calls) {
		this.stats = stats;
		this.calls = calls;
		this.countTitle = $("<div>").addClass("graph-header").text(this.calls["CallsPerEndpointLabel"]);
		this.callCountgraph = $("<div>").css({ "width": "780px", "height": "300px", "margin": "auto"});
		this._buildCountsGraph(this.stats);
		
		
		this.statDetails = $("<table>").addClass("stats-table");
		var header = $("<tr>");
		var endpoint = $("<td>").text(this.calls["EndpointLabel"]).addClass("endpoint");
		var totalCalls = $("<td>").text(this.calls["CallCountLabel"]).addClass("center-text");
		var avgTime = $("<td>").text(this.calls["AverageTimeLabel"]);
		this.statDetails.append(header.append(endpoint, totalCalls, avgTime));
		var details = this.statDetails;
		$.each(this.stats, function(index, stat) {
			var endpointRow = $("<tr>");
			var endpointName = stat[API]? stat[API] : stat["Uri"];
			var endpoint = $("<td>").addClass("endpoint").html("<b>" + endpointName + "</b>");
			var totalCalls = $("<td>").addClass("center-text").text(stat["Call Count"]);
			var avgTime = $("<td>").text(Number(stat["Average Time Between Calls"] / 1000).toFixed(3) + calls["SecondsLabel"]);

			endpointRow.append(endpoint, totalCalls, avgTime);
			details.append(endpointRow);
		});	
		
		var callCountGraphData = this.callCountGraphData;
		
		$.each(stats, function(index, stat) {
			callCountGraphData.push([ stat["Call Count"],stat[API] ]);
		});
		
		this.timelineTitle = $("<div>").addClass("graph-header").text(this.calls["CallsPerSecondLabel"]);
		this.timelineGraph = $("<div>").css({ "width": "780px", "height": "300px", "margin": "auto"});
		var timelineGraphData = this.timelineGraphData;
		var startTime = calls["Start Time"];
		var endTimeRel = Number((calls["End Time"] - startTime) / 1000).toFixed(0);
		var maxHeight = 0;
		
		$.each(this.calls["Call List"], function(index, endpoint) {
			var endpointData = {
				label: endpoint[API],
				data: [],
				endpoint: endpoint
			};
			var callsPerSecond = {};
			$.each(endpoint.Calls, function(index, call) {
				var relTime = ((call.ReqTime - startTime) / 1000).toFixed(0);
				if(callsPerSecond[relTime.toString()] === undefined)
				{
					callsPerSecond[relTime.toString()] = 0;
				}
				callsPerSecond[relTime.toString()]++;
			});
			
			if(callsPerSecond["0"] === undefined)
			{
				endpointData.data.push([0,0]);
			}
			
			for(second in callsPerSecond)
			{
				var secondNum = Number(second);
				
				if(callsPerSecond[(secondNum - 1).toString()] === undefined)
				{
					endpointData.data.push([ secondNum - 1, 0 ]);
				}
				
				endpointData.data.push([ secondNum, callsPerSecond[second] ]);
				
				if(callsPerSecond[second] > maxHeight) {
					maxHeight = callsPerSecond[second];
				}
				
				if(callsPerSecond[(secondNum + 1).toString()] === undefined)
				{
					endpointData.data.push([ secondNum + 1, 0 ]);
				}
			}
			
			timelineGraphData.push(endpointData)
		});
		
		this.timelineGraphOptions.xaxis.zoomRange = [5,endTimeRel];
		this.timelineGraphOptions.xaxis.panRange = [0,endTimeRel];
		this.timelineGraphOptions.xaxis.max = endTimeRel;
		
		this.timelineGraphOptions.yaxis.zoomRange = [5,maxHeight * 1.1];
		this.timelineGraphOptions.yaxis.panRange = [0,maxHeight * 1.1];
		this.timelineGraphOptions.yaxis.max = maxHeight * 1.1;

	},
	show: function(element) {
		element.append(this.countTitle, this.callCountgraph, this.timelineTitle, this.timelineGraph, this.statDetails);
	},
	changeAPI: function (API) {
		var callCountGraphData = [];

		$.each(stats, function (index, stat) {
			callCountGraphData.push([stat["Call Count"], stat[API]]);
		});

		this.callCountGraphData = callCountGraphData;
		this._buildCountsGraph();
		
		this.statDetails.empty();
		var header = $("<tr>").addClass("header-row");
		var endpoint = $("<td>").text(this.calls["EndpointLabel"]).addClass("endpoint");
		var totalCalls = $("<td>").text(this.calls["CallCountLabel"]).addClass("center-text");
		var avgTime = $("<td>").text(this.calls["AverageTimeLabel"]).addClass("center-text");
		this.statDetails.append(header.append(endpoint, totalCalls, avgTime));
		var details = this.statDetails;
		$.each(this.stats, function (index, stat) {
			var endpointRow = $("<tr>");
			var endpointName = stat[API]? stat[API] : stat["Uri"];
			var endpoint = $("<td>").addClass("endpoint").html(endpointName);
			var totalCalls = $("<td>").addClass("center-text").text(stat["Call Count"]);
			var avgTime = $("<td>").addClass("time").text(Number(stat["Average Time Between Calls"] / 1000).toFixed(3) + calls["SecondsLabel"]);

			endpointRow.append(endpoint, totalCalls, avgTime);
			details.append(endpointRow);
		});
		
		$.each(this.timelineGraphData, function(index, endpoint) {
			endpoint.label = endpoint.endpoint[API];
		});
		
		this._buildCountsGraph();
		this._buildTimelineGraph();
	},
	_buildCountsGraph: function() {
		this.callCountgraph.empty();
		$.plot(this.callCountgraph, [ this.callCountGraphData ], this.callCountGraphOptions);
	},
	_buildTimelineGraph: function() {
		this.timelineGraph.empty();
		$.plot(this.timelineGraph, this.timelineGraphData, this.timelineGraphOptions);
	}
}

var getRule = function(ruleName, rules){
	for(var i = 0; i < rules.length; ++i){
		if(rules[i].Name === ruleName)
		{
			return rules[i];
		}
	} 
	return null;
}

var getUrlParameter = function getUrlParameter(sParam) {
	var sPageURL = decodeURIComponent(window.location.search.substring(1)),
		sURLVariables = sPageURL.split('&'),
		sParameterName,
		i;

	for (i = 0; i < sURLVariables.length; i++) {
		sParameterName = sURLVariables[i].split('=');

		if (sParameterName[0] === sParam) {
			return sParameterName[1] === undefined ? true : sParameterName[1];
		}
	}
};

function toggleExpanderElemement(obj) {
	if (obj.data[0].expanded === true) {
		obj.data[0].expandElement.hide();
		obj.data[0].expanded = false;

		obj.data[1].removeClass("atg-expander-expanded");

		if (obj.data[2] != undefined) {
			obj.data[2].removeClass("expanded");
		}
	}
	else {
		obj.data[0].expandElement.show();
		obj.data[0].expanded = true;

		obj.data[1].addClass("atg-expander-expanded");

		if (obj.data[2] != undefined) {
			obj.data[2].addClass("expanded");
		}
	}
};

$.fn.fixThis = function () {
	return this.each(function (index) {
		var $this = $(this), $t_fixed;
		function init() {
			$this.wrap('<div />');
			$t_fixed = $this.clone();
			$t_fixed.removeAttr("id");
			$t_fixed.addClass("endpoint-header-fixed").insertBefore($this).width($this.width());
			var containerPosition = $(".result-container").offset().top;
			$t_fixed.css("top", containerPosition + "px");
			$t_fixed.hide();
		}
		function scrollFixed() {
			var offset = $(this).offset().top,
			tableOffsetTop = $this.parent().parent().offset().top,
			tableOffsetBottom = tableOffsetTop + $this.parent().parent().parent().height();

			if ($t_fixed.is(":hidden") == false && (offset < tableOffsetTop || offset > tableOffsetBottom)) {
				$t_fixed.hide();
			}
			else if (offset >= tableOffsetTop && offset <= tableOffsetBottom && $t_fixed.is(":hidden")) {
				$t_fixed.show();
			}
		}
		function scrollFixedWindow() {
			var offset = $(".result-container").offset().top;
			$t_fixed.css("top", offset - $(window).scrollTop());
		}
		var container = $(".result-container");
		container.scroll(scrollFixed);
		$(window).scroll(scrollFixedWindow);
		init();
	});
};















