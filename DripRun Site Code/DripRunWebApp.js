import { handleNotAdminAuth } from 'backend/auth-handler.web';
import { session } from 'wix-storage-frontend';
import wixWindowFrontend from 'wix-window-frontend';
import wixLocationFrontend from 'wix-location-frontend';

$w.onReady(function () {

    if (session.getItem('jwtToken') == null) {
        wixLocationFrontend.to('https://www.driprun.com.au/login');
    };

    $w('#button1').onClick(handleLogout);

    // $w('#button1').onClick(async () => {
    //     const httpResponse = await handleNotAdminAuth('logout', '');
    //     if (httpResponse == true) {
    //         session.setItem('accType', null);
    //         session.setItem('idToken', null);
    //         session.setItem('name', null);
    //         session.setItem('email', null);
    //         session.setItem('jwtToken', null);
    //         wixLocationFrontend.to('https://www.driprun.com.au/login');
    //     };
    // });

    $w('#button2').onClick(() => {
        toastHandler('Aww. Sad to see you go :(',
                     'Press the button to confirm deletion of your account.',
                     '',
                     '');
    });

    $w('#html1').onMessage((event) => { // send creds to unity when it's ready
        if (event.data == 'UnityReady' && session.getItem('jwtToken') != null) {
            unityCredentialHandler();
        }
        else {
            wixLocationFrontend.to('https://www.driprun.com.au/login');
        };
    });

    setTimeout(function() {
        unityCredentialHandler() // also send creds to unity at page refresh
    }, (1000)); // in milliseconds, small delay in case of slowe cache loading

});

function unityCredentialHandler () {

    // const name = session.getItem('name');
    // const email = session.getItem('email');
    const jwtToken = session.getItem('jwtToken');
    $w('#html1').postMessage({
        messageType: 'dataUpdate',
        payload: {
            'name': "",
            'email': "",
            'accessToken': jwtToken
        }
    });

}

function toastHandler (title, content, deleteType, deleteId) {

    const toastInfo = {
        toastTitle: title,
        toastContent: content,
        deleteType: deleteType,
        deleteId: deleteId
    };
    wixWindowFrontend.openLightbox('Toast', toastInfo);
};

async function handleLogout()
{
    try {
        const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/logout-web`, {
            method: 'POST',
            credentials: 'include'
        });
        if (!httpResponse.ok) {
            const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
            throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
        };

        //return httpResponse.ok;
        session.setItem('accType', null);
        session.setItem('idToken', null);
        session.setItem('name', null);
        session.setItem('email', null);
        session.setItem('jwtToken', null);
        wixLocationFrontend.to('https://www.driprun.com.au/login');
    }
    catch (err) {
        console.log('handleAuthLogout -', err);
        return null;
    };
}