var excList = [];
var excRemaining = {};
var excRemainingList = [];
var socket;
var selectize;

window.onload = function () {

    console.log("ready");
    $('body').find('.page.train').hide();
    $('body').find('.page.options').hide();


    // CLOCKPICKER ******************************************************

    $('.clockpicker').clockpicker().find('input').change(function () {
        // TODO: time changed
        // var time = this.value.split(":");
        // var cd = new Date();
        // cd.setHours(time[0]);
        // cd.setMinutes(time[1]);
        // console.log("Timestamp: "+cd.getTime());
    });
    $('#demo-input').clockpicker({
        autoclose: true
    });


    // CLOCKPICKER END **************************************************


    // TIMER ************************************************************

    var Timer = function (canvasId, timeMax, refreshRate, timeoutCallback, strokeColor, fillColor) {
        var canvas = document.getElementById(canvasId),
            ctx = canvas.getContext("2d"),
            cwidth = canvas.width,
            cheight = canvas.height,
            timeMax = timeMax,
            timeLeft = timeMax,
            refreshRate = refreshRate,
            middleX = cwidth / 2,
            middleY = cheight / 2,
            maxRadius = cwidth / 2,
            strokeColor = strokeColor || "#000",
            fillColor = fillColor || "red",
            radianStart = Math.PI - 0.5,
            radianMax = Math.PI * 2,
            arcNorth = Math.PI * -0.5,
            stepTimer = null;

        var redrawInitials = function () {
            // lazy def
            ctx.strokeStyle = strokeColor;
            ctx.fillStyle = fillColor;
            ctx.lineWidth = .5;
            redrawInitials = function () {
                ctx.clearRect(0, 0, cwidth, cheight);
                ctx.beginPath();
                ctx.arc(middleX, middleY, maxRadius, 0, radianMax, false);
                ctx.stroke();
            };
            redrawInitials();
            return redrawInitials;
        }

        var proceed = function () {
            var newTimeLeft = timeLeft - refreshRate
            timerObj.setTimeLeft(newTimeLeft) && drawTimestep();
            if (newTimeLeft < 0) {
                timerObj.stop();
                if (timeoutCallback) {
                    timeoutCallback.call(null, newTimeLeft);
                }
            }
        }

        var drawTimestep = function () {
            var timeUsedPercent = (timeMax - timeLeft) / timeMax;
            var rmax = radianMax * timeUsedPercent;
            ctx.beginPath();
            ctx.moveTo(middleX, middleY);
            ctx.arc(middleX, middleY, maxRadius, arcNorth, arcNorth + rmax, false);
            ctx.fill();
        }

        var drawTimestep2 = function (total, used) {
            var timeUsedPercent = used / total;
            var rmax = radianMax * timeUsedPercent;

            redrawInitials();

            ctx.beginPath();
            ctx.moveTo(middleX, middleY);
            ctx.arc(middleX, middleY, maxRadius, arcNorth, arcNorth + rmax, false);
            ctx.fill();
        }

        var timerObj = {
            setTimeLeft: function (time) {
                if (time > timeMax) {
                    return false;
                } else if (time > timeLeft) redrawInitials();
                timeLeft = time;
                return true;
            },

            start: function () {
                stepTimer = setInterval(proceed, refreshRate);
            },

            draw: function (total, used) {
                drawTimestep2(total, used);
            },

            stop: function () {
                clearInterval(stepTimer);
            }
        };

        var init = (function () {
            redrawInitials();
            timerObj.setTimeLeft(timeMax);
        })()

        return timerObj;
    };
    var homeTimer = new Timer("countdown", 5000, 50, function () { }, "red", "green");
    // TIMER END ********************************************************

    // SELECTIZE ********************************************************

    var $select = $('#select-label').selectize({
        create: true,
        sortField: {
            field: 'text',
            direction: 'asc'
        },
        dropdownParent: 'body'
    });

    selectize = $select[0].selectize;
    console.log(selectize);
    selectize.on('focus', function (e) {
        socket.send(JSON.stringify({
            page: "train",
            operation: "labels",
            data: {}
        }));

    });

    // SELECTIZE END ****************************************************
    // TAB SWITCH MODE **************************************************
    $('li.gototab').click(function (e) {
        $(e.target).parent().parent().find('li.active').removeClass('active');
        $(e.target).parent().addClass('active');
        var target = $(e.target).text().toLowerCase();
        $('body').find('.page').hide();
        $('body').find('.page.' + target).show();
        if (target == 'train') {
            $("#canvas").detach().appendTo('#trainCanvas');
            socket.send(JSON.stringify({
                page: "train",
                operation: "mode",
                data: {}
            }));
        } else if (target == 'home') {
            $("#canvas").detach().appendTo('#testCanvas');
            socket.send(JSON.stringify({
                page: "home",
                operation: "mode",
                data: {}
            }));
        }
    });
    // TAB SWITCH MODE **************************************************

    // OPTIONS TABLE ****************************************************
    var rowCount = 1;
    $("#add_row").click(function () {
        $('#addr' + rowCount).html("<td><input name='exe" + rowCount + "' type='text' placeholder='Exercise' class='form-control input-md'  /> </td><td><input  name='count" + rowCount + "' type='text' placeholder='Count'  class='form-control input-md'></td>");

        $('#tab_logic').append('<tr id="addr' + (rowCount + 1) + '"></tr>');
        rowCount++;
    });
    $("#delete_row").click(function () {
        if (rowCount > 1) {
            $("#addr" + (rowCount - 1)).html('');
            rowCount--;
        }
    });
    $("#save_settings").click(function () {
        $('#timeLeft').text($('.clockpicker').clockpicker().find('input').val());
        var time = $('.clockpicker').clockpicker().find('input').val().split(":");
        var cd = new Date();
        cd.setHours(time[0]);
        cd.setMinutes(time[1]);
        var exercises = "";
        var e, c;
        $('#testExeTable').empty();
        for (var i = 0; i < rowCount; i++) {
            e = $("input[name='exe" + i + "']").val();
            c = $("input[name='count" + i + "']").val();
            $('#testExeTable').append("<tr><td>" + e + "</td><td>" + c + "</td></tr>");

            for (var j = 0; j < parseInt(c) ; j++) {
                exercises += e;
                exercises += "|";
            }
        };
        exercises = exercises.substr(0, exercises.length - 1);
        console.log(exercises);
        socket.send(JSON.stringify({
            page: "options",
            operation: "setdt",
            data: {
                hour: time[0],
                minute: time[1],
                exc: exercises
            }
        }));
    });
    // OPTIONS TABLE ****************************************************


    var status = document.getElementById("status");
    var canvas = document.getElementById("canvas");
    var context = canvas.getContext("2d");

    if (!window.WebSocket) {
        status.innerHTML = "Your browser does not support web sockets!";
        return;
    }

    status.innerHTML = "Connecting to server...";

    // Initialize a new web socket.
    socket = new WebSocket("ws://138.51.175.165:8185");

    // Connection established.
    socket.onopen = function () {
        status.innerHTML = "Connection successful.";
        socket.send(JSON.stringify({
            page: "home",
            operation: "mode",
            data: {}
        }));
    };

    // Connection closed.
    socket.onclose = function () {
        status.innerHTML = "Connection closed.";
    }

    // Receive data FROM the server!
    socket.onmessage = function (event) {
        if (typeof event.data === "string") {

            // Get the data in JSON format.
            var jobj = JSON.parse(event.data);
            switch (jobj.page) {
                case "home":
                    //clear canvas!
                    context.clearRect(0, 0, canvas.width, canvas.height);
                    var skeleton = jobj.data.skeleton;
                    // Display the skeleton joints.
                    for (var j = 0; j < skeleton.joints.length; j++) {
                        var joint = skeleton.joints[j];
                        // Draw!!!
                        context.strokeStyle = "#0000FF";
                        context.fillStyle = "#0000FF";
                        context.beginPath();
                        context.arc(joint.x, joint.y, 2, 0, Math.PI * 2, true);
                        context.stroke();
                    }
                    console.log(jobj.data.alarmOn);
                    if (jobj.data.alarmOn) {
                        homeTimer.draw(jobj.data.avgFrame, jobj.data.curFrame);
                    } else {
                        homeTimer.draw(jobj.data.avgFrame, jobj.data.curFrame);
                    }
                    if (JSON.stringify(jobj.data.excRemaining) != JSON.stringify(excRemainingList)) {
                        excList = [];
                        excRemaining = {};
                        excRemainingList = jobj.data.excRemaining;
                        jobj.data.excRemaining.map(function (a) {
                            if (a in excRemaining) excRemaining[a]++;
                            else {
                                excRemaining[a] = 1;
                                if (excList.indexOf(a) == -1) excList.push(a);
                            }
                        });
                        $('#testExeTable').empty();
                        for (var i = 0; i < excList.length; i++) {
                            $('#testExeTable').append("<tr><td>" + excList[i] + "</td><td>" + excRemaining[excList[i]] + "</td></tr>");
                        }
                    }
                    break;



                case "train":
                    switch (jobj.operation) {
                        case "labels":
                            selectize.clearOptions();
                            var tmp = [];
                            for (var i = 0; i < jobj.data.length; i++) {
                                tmp.push({ value: jobj.data[i], text: jobj.data[i] });
                            }
                            selectize.addOption(tmp);
                            selectize.refreshOptions();
                            break;
                    }
                    break;



                case "options":
                    break;
            }


        }
    };

};

// TRAINING PAGE FUNCTIONS ******************************************

function acceptTraining() {
    socket.send(JSON.stringify({
        page: "train",
        operation: "accept",
        data: {
            label: $('.selectize-input').find('div.item').text()
        }
    }));
}

function rejectTraining() {
    socket.send(JSON.stringify({
        page: "train",
        operation: "reject",
        data: {}
    }));
}

function train() {
    socket.send(JSON.stringify({
        page: "train",
        operation: "train",
        data: {}
    }));
}