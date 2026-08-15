import { getMissionInfo, toggleMissionStatus, createMission, getCouponLocation, createCoupon, deleteUploadedImg} from 'backend/mission-drop-handler.web';
import { session } from 'wix-storage-frontend';
import wixWindowFrontend from 'wix-window-frontend';
import wixLocationFrontend from 'wix-location-frontend';

let modelFileUrl = null;

$w.onReady(function () {

    if (session.getItem('jwtToken') == null) {
        wixLocationFrontend.to('https://www.driprun.com.au/admin-login');
    }
    else {
        timeoutHandler();
        getDashboardInfo();
    };

    $w('#htmlModelUpload').postMessage({
        type: "init",
        jwt: session.getItem('jwtToken')
    });

    $w('#uploadButton3').collapse();
    $w('#uploadButtonLogo').collapse();

    $w('#uploadButton2').onChange(() => {
        // 🔥 Clear model state
        modelFileUrl = null;
        // 🔥 Reset iframe UI
        $w('#htmlModelUpload').postMessage({ type: "reset" });
    });

    $w('#htmlModelUpload').onMessage((event) => {
        modelFileUrl = event.data.fileUrl;
    
        // 🔥 Clear image button
        $w('#uploadButton2').reset();

        // 🔥 force UI refresh (Wix hack)
        $w('#uploadButton2').collapse();
        setTimeout(() => {
            $w('#uploadButton2').expand();
        }, 50);
    
        // 🔥 Hide secondary upload (Button 2)
        $w('#uploadButton3').collapse();
    
        // 🔥 Also clear its value just in case
        try {
            $w('#uploadButton3').reset();
        } catch {}
    });
   
    $w('#dropdownRarity').options = [
        { label: "Common", value: "Common" },
        { label: "Uncommon", value: "Uncommon" },
        { label: "Epic", value: "Epic" },
        { label: "Legendary", value: "Legendary" },
        { label: "Ultra", value: "Ultra" },
        { label: "Raid", value: "Raid" },
        { label: "One-Off", value: "OneOff" }
        ];
        $w('#dropdownRarity').selectedIndex = 0;
        const rarity = $w('#dropdownRarity').value;

        $w('#dropdownRarity').onChange(() => {
            const rarity = $w('#dropdownRarity').value;
        
            // 🔥 If model is selected → NEVER show button 2
            if (modelFileUrl) {
                $w('#uploadButton3').collapse();
                $w('#uploadButtonLogo').expand();
                return;
            }
        
            if (rarity === "Raid" || rarity === "OneOff") {
                $w('#uploadButton3').expand();
                $w('#uploadButtonLogo').expand();
            } else {
                $w('#uploadButton3').collapse();
                $w('#uploadButtonLogo').collapse();
        
                try {
                    $w('#uploadButton3').reset();
                    $w('#uploadButtonLogo').reset();
                } catch {}
            }
        });


    $w('#dropdown1').onChange(() => {
        getDashboardInfo();
    });


    $w('#button1').onClick(() => { // no need to check if all missions since the button will be disabled
        $w('#section12').collapse(); // show new coupon form and hide everything else
        $w('#section13').collapse();
        $w('#section14').collapse();
        $w('#section15').collapse();
        $w('#section16').collapse();
        $w('#section18').collapse();
        $w('#section19').collapse();
        $w('#section21').collapse();
        $w('#section22').expand();
    });

    $w('#button2').onClick(() => { // no need to check if all missions since the button will be disabled
        const missionName = $w('#dropdown1').options[$w('#dropdown1').selectedIndex].label;
        toastHandler('Are you sure?',
                     `Press the button to confirm deletion of ${missionName}.`,
                     'mission',
                     $w('#dropdown1').value);
    });

    $w('#button3').onClick(() => { // no need to check if all missions since the button will be disabled
        $w('#section12').collapse(); // show new coupon form and hide everything else
        $w('#section13').collapse();
        $w('#section14').collapse();
        $w('#section15').collapse();
        $w('#section16').collapse();
        $w('#section18').collapse();
        $w('#section19').collapse();
        $w('#section21').collapse();
        $w('#section23').expand();
    });

    $w('#button4').onClick(async () => { // no need to check if all missions since the button will be disabled
        const missionId = $w('#dropdown1').value;
        const jwtToken = session.getItem('jwtToken');
        const jsonStatusResponse = await toggleMissionStatus(missionId, jwtToken);
        $w('#text54').text = `Currently: ${jsonStatusResponse.status}`;
        if (jsonStatusResponse.status == 'Active') {
            $w('#button4').label = 'Hide Mission';
        }
        else {
            $w('#button4').label = 'Show Mission';
        };
    });

    $w('#button5').onClick(() => { // revert to All Time
        $w('#datePicker1').value = null; // default to All Time
        $w('#datePicker2').value = new Date(); // default to today
        getStatsInfo();
    });

    $w('#datePicker1').onChange(() => {
        if ($w('#datePicker1').value.getTime() >= $w('#datePicker2').value.getTime()) { // if start date after or same as end date
            var tempDate = new Date();
            tempDate.setDate($w('#datePicker1').value.getDate() + 1)
            $w('#datePicker2').value = tempDate;
        };
        getStatsInfo();
    });

    $w('#datePicker2').onChange(() => {
        if ($w('#datePicker1').value == null) { // if 'All Time'
            var tempDate = new Date();
            tempDate.setDate($w('#datePicker2').value.getDate() - 1)
            $w('#datePicker1').value = tempDate;
        }
        else if ($w('#datePicker1').value.getTime() >= $w('#datePicker2').value.getTime()) { // if start date after or same as end date
            var tempDate = new Date();
            tempDate.setDate($w('#datePicker1').value.getDate() + 1)
            $w('#datePicker2').value = tempDate;
        };
        getStatsInfo();
    });

    $w('#html7').onMessage(async (event) => {
        if (event.data[0] == 'loc') {
            const jwtToken = session.getItem('jwtToken');
            const jsonCouponLocResponse = await getCouponLocation(event.data[1], jwtToken);
            const arrayCouponLocation = [];
            jsonCouponLocResponse.locations.forEach(location => {
                arrayCouponLocation.push('Coords: ' + location.latitude + '\t' + location.longitude + '\n' +
                                         'Name: ' + location.locationName + '\n');
            });
            toastHandler('Coupon ' + event.data[1] + ' locations',
                         arrayCouponLocation.join('------------------------------------------------------------\n'),
                         '');
        }
        else if (event.data[0] == 'del') {
            toastHandler('Are you sure?',
                         `Press the button to confirm deletion of coupon ID ${event.data[1]}.`,
                         'coupon',
                         event.data[1]);
        };
    });

    $w('#section22').collapse(); // hide the forms
    $w('#section23').collapse(); // hide the forms

    $w('#button6').onClick(() => { // submit new mission form
        createMissionHandler('submit');
    });

    $w('#button7').onClick(() => { // cancel creating mission
        createMissionHandler('cancel');
    });

    $w('#button8').onClick(() => { // subbmit new coupon form
        createCouponHandler('submit');
    });

    $w('#button9').onClick(() => { // cancel creating coupon
        createCouponHandler('cancel');
    });

});

async function getDashboardInfo() {

    const jwtToken = session.getItem('jwtToken');
    const jsonMissionsResponse = await getMissionInfo('missions', jwtToken, '1');
    if (jsonMissionsResponse != null) {
        session.setItem('validJwtToken', 'true');

        const arrayMissionName = [];
        jsonMissionsResponse.missions.forEach(mission => {
            arrayMissionName.push({label: mission.name, value: String(mission.missionId)});
        });
        $w('#dropdown2').options = arrayMissionName; // the dropdown in new drops form don't need all missions
        arrayMissionName.push({label: 'All Missions', value: '-1'});
        $w('#dropdown1').options = arrayMissionName;

        const previousIndex = session.getItem('previousIndex');
        if (previousIndex != null && previousIndex != 'null') { // retain previous selected index after adding or deleting new coupon
            $w('#dropdown1').selectedIndex = parseInt(previousIndex);
            $w('#dropdown2').selectedIndex = parseInt(previousIndex);
            session.setItem('previousIndex', null);
        }
        else if ($w('#dropdown1').selectedIndex == undefined) { // default to first option
            $w('#dropdown1').selectedIndex = 0;
            $w('#dropdown2').selectedIndex = 0;
        };

        if ($w('#dropdown1').value == '-1') {
            $w('#button2').disable();
            $w('#button4').disable();
            $w('#text22').text = 'N/A';
            $w('#text25').text = 'N/A';
        }
        else {
            $w('#dropdown2').selectedIndex = $w('#dropdown1').selectedIndex;
            $w('#button2').enable();
            $w('#button4').enable();
            jsonMissionsResponse.missions.some(mission => {
                if (String(mission.missionId) == $w('#dropdown1').value) {
                    $w('#text22').text = mission.locationName;
                    $w('#text25').text = String(mission.couponCount); 
                    return true; // skip the rest of array if matched
                }
            });
        };

        $w('#datePicker1').value = null; // default to All Time
        $w('#datePicker2').value = new Date(); // default to today
        getStatsInfo();
    }
    else {
        if (session.getItem('validJwtToken') == 'true') {
            toastHandler('Ah. Signin expired',
                         'You have been here for so long that the signin authentication expired. Please signin again.',
                         '',
                         '');
        }
        else {
            toastHandler('Oops... Unauthorised',
                         'If you would like to become an admin, please contact Drip Run.',
                         '',
                         '');
        };
    };

};

async function getStatsInfo() {

    const date1 = $w('#datePicker1').value;
    const date2 = $w('#datePicker2').value;
    const jwtToken = session.getItem('jwtToken');
    var jsonStatsResponse = {};
    if (date1 != null) {
        const startDate = `${date1.getFullYear()}-${date1.getMonth()+1}-${date1.getDate()}`; // Month +1 cuz js Date be like 0-11
        const endDate = `${date2.getFullYear()}-${date2.getMonth()+1}-${date2.getDate()}`; // Month +1 cuz js Date be like 0-11
        jsonStatsResponse = await getMissionInfo(`stats?missionId=${$w('#dropdown1').value}&start=${startDate}&end=${endDate}`, jwtToken, '2');
    }
    else {
        jsonStatsResponse = await getMissionInfo(`stats?missionId=${$w('#dropdown1').value}`, jwtToken, '3'); // All Time
    };

    if (jsonStatsResponse != null) {
        if ($w('#dropdown1').value == '-1') {
            $w('#text54').text = `Currently: N/A`;
            $w('#button4').label = 'Hide Mission';
        }
        else if (jsonStatsResponse.summary.missionStatus == 'Active') {
            $w('#text54').text = `Currently: ${jsonStatsResponse.summary.missionStatus}`;
            $w('#button4').label = 'Hide Mission';
        }
        else {
            $w('#text54').text = `Currently: ${jsonStatsResponse.summary.missionStatus}`;
            $w('#button4').label = 'Show Mission';
        };

        $w('#text28').text = String(jsonStatsResponse.splits.uniqueImpressionUsers);
        $w('#html1').postMessage({
            messageType: 'dataUpdate',
            payload: jsonStatsResponse.splits.genderPct
        });
        $w('#html2').postMessage({
            messageType: 'dataUpdate',
            payload: jsonStatsResponse.splits.ageRangePct
        });
        $w('#html5').postMessage({
            messageType: 'dataUpdate',
            payload: jsonStatsResponse.newPlayers.daily
        });
        $w('#text40').text = String(jsonStatsResponse.totals.impressions);
        $w('#text42').text = String(jsonStatsResponse.totals.totalUserCoupons);
        $w('#text44').text = String(jsonStatsResponse.totals.avgDropsPerViewer);
        $w('#html7').postMessage({
            messageType: 'dataUpdate',
            payload: jsonStatsResponse.coupons
        });
    };

};

async function createMissionHandler(eventType) {

    if (eventType == 'submit' && $w('#input1').value != '' && $w('#uploadButton1').value.length > 0 && $w('#checkbox2').checked == true) { // if all required fields are completed
        const missionInfo = [];
        missionInfo.push($w('#input1').value); // Name
        missionInfo.push($w('#input2').value); // Description
        missionInfo.push($w('#input3').value); // Location Name
        missionInfo.push($w('#checkbox1').checked); // isVisible
        await $w('#uploadButton1')
        .uploadFiles()
        .then((uploadedFiles) => {
            uploadedFiles.forEach((uploadedFile) => {
                missionInfo.push(uploadedFile.fileUrl); // missionImageUrl
                missionInfo.push(uploadedFile.originalFileName); // ImgName
            });
        });

        // image resolution check
        const missionImgParams = new URLSearchParams(new URL(`${missionInfo[4]}`).hash.slice(1));
        const missionImgWidth = parseInt(missionImgParams.get('originWidth'));
        const missionImgHeight = parseInt(missionImgParams.get('originHeight'));
        const jwtToken = session.getItem('jwtToken');
        // createMission(missionInfo, jwtToken);
        hideFormsShowDashboard();
        if (missionImgWidth == missionImgHeight) {
            const jwtToken = session.getItem('jwtToken');
            createMission(missionInfo, jwtToken);
            hideFormsShowDashboard();
        }
        else {
            deleteUploadedImg([missionInfo[4]]); // remove mission image form wix storage because wix automatically saves it
            $w('#uploadButton1').reset();
            toastHandler('Oh no. Image is not square',
                         'Please ensure your image has a 1:1 aspect ratio (length = width) and upload again.',
                         '',
                         '')
        };
    }
    else if (eventType == 'cancel') {
        hideFormsShowDashboard();
    }
    else {
        toastHandler('Hmm. Info Missing',
                     'Please complete all required fields.',
                     '',
                     '')
    };

};

async function createCouponHandler(eventType) {

    if (
        eventType == 'submit' &&
        $w('#textBox1').value.length > 0 &&
        $w('#checkbox5').checked == true
    ) {

        const couponInfo = [];

        // --- Core ---
        couponInfo.push($w('#dropdown2').value); // 0 MissionId
        couponInfo.push($w('#input4').value);    // 1 DropName
        couponInfo.push($w('#input5').value);    // 2 ShopifyLink

        const shopifyLink = ($w('#input5').value || '').trim();
        const descriptionInput = ($w('#inputDescription')?.value || '').trim();

        if (shopifyLink === '' && descriptionInput === '') {
            toastHandler(
                'Missing Content',
                'Please provide either a Shopify Link or a Description (or both).',
                '',
                ''
            );
            return;
        }

        // --- TOS ---
        if ($w('#input6').value != '') {
            couponInfo.push($w('#input6').value); // 3
        } else {
            couponInfo.push('https://www.driprun.com.au/terms-and-conditions');
        }

        // --- Rarity ---
        let rarity = "Common";
        try {
            rarity = $w('#dropdownRarity')?.value || "Common";
        } catch {}
        couponInfo.push(rarity); // 4

        couponInfo.push($w('#checkbox3').checked); // 5

        // --- Locations ---
        const lat = [], lon = [], names = [];

        $w('#textBox1').value.split('\n').forEach((line) => {
            const parts = line.replace(/\s+/g, '').split(',');
            lat.push(parts[0]);
            lon.push(parts[1]);
            names.push(parts[2]);
        });

        couponInfo.push(lat);   // 6
        couponInfo.push(lon);   // 7
        couponInfo.push(names); // 8

        // --- MODEL OR IMAGE (REQUIRED) ---

        let primaryUrl = null;

        // Try image first
        if ($w('#uploadButton2').value.length > 0) {
            const files = await $w('#uploadButton2').uploadFiles();
            primaryUrl = files[0].fileUrl;
        
            // 🔥 Clear model state
            modelFileUrl = null;
        
            // 🔥 Tell iframe to reset UI
            $w('#htmlModelUpload').postMessage({ type: "reset" });
        
            // 🔥 Re-enable Button 2 if needed
            const rarity = $w('#dropdownRarity').value;
            if (rarity === "Raid" || rarity === "OneOff") {
                $w('#uploadButton3').expand();
            }
        }

        // If no image, use model
        if (!primaryUrl && modelFileUrl) {
            primaryUrl = modelFileUrl;
        }

        if (!primaryUrl) {
            toastHandler("Missing File", "Upload an image or model", "", "");
            return;
        }

        couponInfo.push(primaryUrl); // 9

        // --- MAIN IMAGE (OPTIONAL) ---
        let mainUrl = null;
        if ($w('#uploadButton3').value.length > 0) {
            const files = await $w('#uploadButton3').uploadFiles();
            mainUrl = files[0].fileUrl;
        }
        couponInfo.push(mainUrl); // 10

        // --- LOGO (OPTIONAL) ---
        let logoUrl = null;
        if ($w('#uploadButtonLogo')?.value.length > 0) {
            const files = await $w('#uploadButtonLogo').uploadFiles();
            logoUrl = files[0].fileUrl;
        }
        couponInfo.push(logoUrl); // 11

        // --- DESCRIPTION ---
        let description = null;
        try {
            description = $w('#inputDescription')?.value || null;
        } catch {}
        couponInfo.push(description); // 12

        // --- VALIDATE ONLY IF IMAGE ---
        const isImage = /\.(png|jpg|jpeg|webp|gif)$/i.test(primaryUrl);

        const jwtToken = session.getItem('jwtToken');

        if (isImage) {
            const params = new URLSearchParams(new URL(primaryUrl).hash.slice(1));
            const w = parseInt(params.get('originWidth'));
            const h = parseInt(params.get('originHeight'));

            if (w !== h) {
                deleteUploadedImg([primaryUrl]);
                $w('#uploadButton2').reset();

                toastHandler(
                    'Oh no. Image is not square',
                    'Image must be 1:1 aspect ratio.',
                    '',
                    ''
                );
                return;
            }
        }

        // --- SEND TO BACKEND ---
        await createCoupon(couponInfo, jwtToken);
        hideFormsShowDashboard();
    }

    else if (eventType == 'cancel') {
        hideFormsShowDashboard();
    }

    else {
        toastHandler(
            'Hmm. Info Missing',
            'Please complete all required fields.',
            '',
            ''
        );
    }
}

function hideFormsShowDashboard() {

    session.setItem('previousIndex', $w('#dropdown2').selectedIndex); // retain previous selected index after adding or deleting new coupon
    wixLocationFrontend.to('https://www.driprun.com.au/admin-dashboard'); // refresh page to fully update info

}

function timeoutHandler () {

    const timeout = session.getItem('timeout');
    if (timeout != null) {
        setTimeout(function() { 
            toastHandler('Tick tock, timeout.',
                         'Press the button to continue current session.',
                         '',
                         '');
        }, (Number(timeout)-60)*1000); // in milliseconds
    };

};

function toastHandler (title, content, deleteType, deleteId) {

    const toastInfo = {
        toastTitle: title,
        toastContent: content,
        deleteType: deleteType,
        deleteId: deleteId
    };
    wixWindowFrontend.openLightbox('Toast', toastInfo);

};
