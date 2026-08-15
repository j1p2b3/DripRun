import { handleAdminAuth } from 'backend/auth-handler.web';
import { session } from 'wix-storage-frontend';
import wixWindowFrontend from 'wix-window-frontend';
import wixLocationFrontend from 'wix-location-frontend';

$w.onReady(function () {

    $w("#html1").onMessage((event) => {
        loginButtonHandler('meta', event.data);
    });

    $w("#html2").onMessage((event) => {
        loginButtonHandler('google', event.data);
    });

    $w("#html3").onMessage((event) => {
        loginButtonHandler('apple', event.data);
    });

});

async function loginButtonHandler (accType, idToken) {

    const authResponse = await handleAdminAuth(accType, idToken);
    if (authResponse != null) {
        session.setItem('accType', accType);
        session.setItem('idToken', idToken);
        session.setItem('email', authResponse.email);
        session.setItem('jwtToken', authResponse.jwtToken);
        session.setItem('timeout', authResponse.expiresIn);
        wixLocationFrontend.to(`https://www.driprun.com.au/admin-dashboard`);
    }
    else {
        toastHandler('Oops... Unauthorised',
                     'If you would like to become an admin, please contact Drip Run.',
                     '',
                     '');
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
